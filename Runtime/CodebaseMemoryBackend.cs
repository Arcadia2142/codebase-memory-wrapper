using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using CodebaseMemory.Wrapper.Options;

namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Provides resilient access to the single wrapped codebase-memory MCP child.
/// </summary>
public sealed class CodebaseMemoryBackend(
    IOptions<WrapperOptions> options,
    ILogger<CodebaseMemoryBackend> logger,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Queue<DateTimeOffset> _recentCrashes = new();
    private readonly WrapperOptions _options = options.Value;
    private ManagedChildProcess? _child;
    private DateTimeOffset _lastUsedAt = DateTimeOffset.UtcNow;
    private int _recordedExitedProcessId;
    private int _restartAttempt;
    private int _activeCalls;
    private long _totalCrashes;

    /// <summary>
    /// Lists tools from the wrapped MCP child.
    /// </summary>
    public Task<ListToolsResult> ListToolsAsync(
        ListToolsRequestParams request,
        CancellationToken cancellationToken) =>
        ExecuteAsync<ListToolsResult>("tools/list", OperationKind.Read, async (child, ct) =>
            await child.Client.ListToolsAsync(request, ct), cancellationToken);

    /// <summary>
    /// Calls a tool on the wrapped MCP child.
    /// </summary>
    public Task<CallToolResult> CallToolAsync(
        CallToolRequestParams request,
        OperationKind kind,
        CancellationToken cancellationToken) =>
        ExecuteAsync<CallToolResult>(request.Name, kind, async (child, ct) =>
            await child.Client.CallToolAsync(request, ct), cancellationToken);

    /// <summary>
    /// Stops the lazy child process after the configured idle interval.
    /// </summary>
    public async Task StopIfIdleAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _activeCalls, 0, 0) > 0) {
            return;
        }

        if (timeProvider.GetUtcNow() - _lastUsedAt < _options.Child.IdleStopAfter) {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken);

        try {
            if (_child is null || Interlocked.CompareExchange(ref _activeCalls, 0, 0) > 0) {
                return;
            }

            if (timeProvider.GetUtcNow() - _lastUsedAt < _options.Child.IdleStopAfter) {
                return;
            }

            logger.LogInformation("Stopping idle codebase-memory child pid={Pid}.", _child.ProcessId);
            await DisposeChildLockedAsync();
        }
        finally {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Captures child lifecycle health for /healthz.
    /// </summary>
    public (string ChildState, int? ChildProcessId, long TotalCrashes, int RecentCrashes, bool CrashLoop) GetHealth()
    {
        if (_child is { HasExited: true } exited) {
            RecordExitedChildLocked(exited);
        }

        lock (_recentCrashes) {
            PruneCrashesLocked(timeProvider.GetUtcNow());
            var crashLoop = _recentCrashes.Count >= _options.Retry.CrashLoopThreshold;
            var state = _child is null ? "idle" : _child.HasExited ? "exited" : "running";
            var pid = state == "running" ? _child?.ProcessId : null;
            return (state, pid, _totalCrashes, _recentCrashes.Count, crashLoop);
        }
    }

    /// <summary>
    /// Stops any live child during wrapper shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync();

        try {
            await DisposeChildLockedAsync();
        }
        finally {
            _lifecycleLock.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(
        string operationName,
        OperationKind kind,
        Func<ManagedChildProcess, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(kind, cancellationToken);
        var canRetry = CanRetry(operationName, kind);
        Exception? lastError = null;

        for (var attempt = 0; attempt <= _options.Retry.MaxRetries; attempt++) {
            try {
                deadline.Token.ThrowIfCancellationRequested();
                var child = await EnsureStartedAsync(deadline.Token);
                return await RunAgainstChildAsync(child, operation, deadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested) {
                lastError = new TimeoutException(
                    $"codebase-memory backend timed out while handling '{operationName}'.",
                    ex);
                await MarkChildFailedAsync(lastError, countCrash: false);
                break;
            }
            catch (Exception ex) when (IsChildFailure(ex)) {
                lastError = ex;
                await MarkChildFailedAsync(ex, countCrash: true);

                if (!canRetry || attempt >= _options.Retry.MaxRetries || deadline.IsCancellationRequested) {
                    break;
                }
            }
        }

        throw new ChildProcessUnavailableException(
            $"codebase-memory backend failed while handling '{operationName}'.",
            lastError);
    }

    private CancellationTokenSource CreateDeadline(OperationKind kind, CancellationToken cancellationToken)
    {
        var timeout = kind switch
        {
            OperationKind.Read => _options.Timeouts.Read,
            OperationKind.Index => _options.Timeouts.Index,
            _ => _options.Timeouts.Write
        };

        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private async Task<T> RunAgainstChildAsync<T>(
        ManagedChildProcess child,
        Func<ManagedChildProcess, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeCalls);

        try {
            if (child.HasExited) {
                throw new ChildProcessUnavailableException("codebase-memory child process exited.");
            }

            return await operation(child, cancellationToken);
        }
        finally {
            _lastUsedAt = timeProvider.GetUtcNow();
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    private async Task<ManagedChildProcess> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_child is { HasExited: false } child) {
            return child;
        }

        await _lifecycleLock.WaitAsync(cancellationToken);

        try {
            if (_child is { HasExited: false } existing) {
                return existing;
            }

            if (_child is { HasExited: true } exited) {
                RecordExitedChildLocked(exited);
            }

            await DisposeChildLockedAsync(resetBackoffIfStable: false);
            await ApplyBackoffAsync(cancellationToken);
            _child = await ManagedChildProcess.StartAsync(_options.Child, logger, loggerFactory, cancellationToken);
            _recordedExitedProcessId = 0;
            return _child;
        }
        finally {
            _lifecycleLock.Release();
        }
    }

    private async Task DisposeChildLockedAsync(bool resetBackoffIfStable = true)
    {
        if (_child is null) {
            return;
        }

        var child = _child;

        if (resetBackoffIfStable &&
            timeProvider.GetUtcNow() - child.StartedAt >= _options.Retry.StableRunResetAfter) {
            _restartAttempt = 0;
        }

        _child = null;
        await child.DisposeAsync();
    }

    private void RecordExitedChildLocked(ManagedChildProcess child)
    {
        lock (_recentCrashes) {
            if (_recordedExitedProcessId == child.ProcessId) {
                return;
            }

            _recordedExitedProcessId = child.ProcessId;
        }

        if (timeProvider.GetUtcNow() - child.StartedAt >= _options.Retry.StableRunResetAfter) {
            _restartAttempt = 0;
        }

        RecordCrash();
        _restartAttempt++;
        logger.LogWarning(
            "codebase-memory child pid={Pid} exited before the next backend request.",
            child.ProcessId);
    }

    private async Task MarkChildFailedAsync(Exception exception, bool countCrash)
    {
        await _lifecycleLock.WaitAsync();

        try {
            if (_child is not null) {
                if (timeProvider.GetUtcNow() - _child.StartedAt >= _options.Retry.StableRunResetAfter) {
                    _restartAttempt = 0;
                }

                await DisposeChildLockedAsync(resetBackoffIfStable: false);
            }

            if (countCrash) {
                RecordCrash();
            }

            _restartAttempt++;
            logger.LogWarning(
                exception,
                "codebase-memory child failed; restartAttempt={Attempt}; countCrash={CountCrash}.",
                _restartAttempt,
                countCrash);
        }
        finally {
            _lifecycleLock.Release();
        }
    }

    private async Task ApplyBackoffAsync(CancellationToken cancellationToken)
    {
        var delay = GetBackoffDelay();

        if (delay > TimeSpan.Zero) {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private TimeSpan GetBackoffDelay()
    {
        return _restartAttempt switch
        {
            <= 1 => TimeSpan.Zero,
            2 => TimeSpan.FromSeconds(1),
            3 => TimeSpan.FromSeconds(2),
            4 => TimeSpan.FromSeconds(5),
            5 => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(30)
        };
    }

    private bool CanRetry(string operationName, OperationKind kind)
    {
        if (kind != OperationKind.Read) {
            return false;
        }

        return _options.Retry.ReadOnlyRetryTools.Contains(operationName, StringComparer.Ordinal);
    }

    private static bool IsChildFailure(Exception exception) =>
        exception is ChildProcessUnavailableException or IOException or ObjectDisposedException ||
        exception.InnerException is IOException or ObjectDisposedException;

    private void RecordCrash()
    {
        lock (_recentCrashes) {
            _totalCrashes++;
            _recentCrashes.Enqueue(timeProvider.GetUtcNow());
            PruneCrashesLocked(timeProvider.GetUtcNow());
        }
    }

    private void PruneCrashesLocked(DateTimeOffset now)
    {
        while (_recentCrashes.Count > 0 &&
               now - _recentCrashes.Peek() > _options.Retry.CrashLoopWindow) {
            _recentCrashes.Dequeue();
        }
    }
}
