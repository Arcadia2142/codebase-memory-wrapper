using Microsoft.Extensions.Options;
using CodebaseMemory.Wrapper.Options;

namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Serializes concurrent MCP requests into one backend call at a time.
/// </summary>
public sealed class SessionRequestDispatcher(
    IOptions<WrapperOptions> options,
    ILogger<SessionRequestDispatcher> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Dictionary<string, LinkedList<QueuedRequest>> _sessionQueues = new(StringComparer.Ordinal);
    private readonly Queue<string> _sessionOrder = new();
    private readonly int _capacity = options.Value.QueueCapacity;
    private string? _currentSessionId;
    private int _pendingCount;
    private int _activeCount;

    /// <summary>
    /// Queues work for a session and completes when the single backend worker runs it.
    /// </summary>
    public Task<T> EnqueueAsync<T>(
        string sessionId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var request = new QueuedRequest<T>(sessionId, operation);

        if (cancellationToken.IsCancellationRequested) {
            return Task.FromCanceled<T>(cancellationToken);
        }

        lock (_gate) {
            if (_pendingCount >= _capacity) {
                throw new QueueFullException(_capacity);
            }

            EnqueueLocked(sessionId, request);
            _pendingCount++;

            RegisterCancellationLocked(request, cancellationToken);
        }

        _available.Release();
        return request.Task;
    }

    /// <summary>
    /// Captures queue counters for health reporting.
    /// </summary>
    public (int Pending, int Active, int Sessions) GetSnapshot()
    {
        lock (_gate) {
            var sessionsWithPendingWork = _sessionQueues.Values.Count(queue => queue.Count > 0);
            return (_pendingCount, _activeCount, sessionsWithPendingWork);
        }
    }

    /// <summary>
    /// Runs queued work in session-draining order.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested) {
            QueuedRequest? request = null;

            try {
                await _available.WaitAsync(stoppingToken);
                request = DequeueNext();

                if (request is null) {
                    continue;
                }

                MarkActive(delta: 1);
                await request.ExecuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            }
            catch (Exception ex) {
                logger.LogError(ex, "Unhandled queue dispatcher error.");
                request?.TryFail(ex);
            }
            finally {
                if (request is not null) {
                    MarkActive(delta: -1);
                }
            }
        }
    }

    private void EnqueueLocked(string sessionId, QueuedRequest request)
    {
        if (!_sessionQueues.TryGetValue(sessionId, out var queue)) {
            queue = new LinkedList<QueuedRequest>();
            _sessionQueues.Add(sessionId, queue);
            _sessionOrder.Enqueue(sessionId);
        }

        request.QueueNode = queue.AddLast(request);
    }

    private QueuedRequest? DequeueNext()
    {
        lock (_gate) {
            var request = DequeueNextLocked();

            if (request is not null) {
                _pendingCount--;
            }

            return request;
        }
    }

    private QueuedRequest? DequeueNextLocked()
    {
        // Keep draining the current session until it has no queued work left.
        while (true) {
            if (_currentSessionId is not null &&
                _sessionQueues.TryGetValue(_currentSessionId, out var currentQueue) &&
                currentQueue.First is not null) {
                var request = currentQueue.First.Value;
                currentQueue.RemoveFirst();
                request.QueueNode = null;
                return request;
            }

            if (_currentSessionId is not null) {
                _sessionQueues.Remove(_currentSessionId);
                _currentSessionId = null;
            }

            if (_sessionOrder.Count == 0) {
                return null;
            }

            var nextSessionId = _sessionOrder.Dequeue();

            if (!_sessionQueues.TryGetValue(nextSessionId, out var nextQueue) || nextQueue.Count == 0) {
                _sessionQueues.Remove(nextSessionId);
                continue;
            }

            _currentSessionId = nextSessionId;
        }
    }

    private void RegisterCancellationLocked(QueuedRequest request, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) {
            return;
        }

        var registration = cancellationToken.Register(static state =>
        {
            var (dispatcher, queuedRequest) = ((SessionRequestDispatcher, QueuedRequest))state!;
            dispatcher.CancelQueuedRequest(queuedRequest);
        }, (this, request));

        request.SetCancellationRegistration(registration);
    }

    private void CancelQueuedRequest(QueuedRequest request)
    {
        var removed = false;

        lock (_gate) {
            if (request.QueueNode is { List: not null } node) {
                node.List.Remove(node);
                request.QueueNode = null;
                _pendingCount = Math.Max(0, _pendingCount - 1);
                removed = true;

                if (_sessionQueues.TryGetValue(request.SessionId, out var queue) &&
                    queue.Count == 0 &&
                    request.SessionId != _currentSessionId) {
                    _sessionQueues.Remove(request.SessionId);
                }
            }
        }

        if (removed) {
            request.TryFail(
                new OperationCanceledException("The HTTP request was cancelled before backend execution."),
                disposeCancellationRegistration: false);
        }
    }

    private void MarkActive(int delta)
    {
        lock (_gate) {
            _activeCount = Math.Max(0, _activeCount + delta);
        }
    }

    private abstract class QueuedRequest(string sessionId)
    {
        private CancellationTokenRegistration _cancellationRegistration;
        private int _completed;

        public string SessionId { get; } = sessionId;

        public LinkedListNode<QueuedRequest>? QueueNode { get; set; }

        public void SetCancellationRegistration(CancellationTokenRegistration registration)
        {
            _cancellationRegistration = registration;

            if (Volatile.Read(ref _completed) == 1) {
                _cancellationRegistration.Dispose();
            }
        }

        public abstract Task ExecuteAsync(CancellationToken cancellationToken);

        public abstract void TryFail(Exception exception, bool disposeCancellationRegistration = true);

        protected bool TryComplete(bool disposeCancellationRegistration = true)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) {
                return false;
            }

            if (disposeCancellationRegistration) {
                _cancellationRegistration.Dispose();
            }

            return true;
        }
    }

    private sealed class QueuedRequest<T>(
        string sessionId,
        Func<CancellationToken, Task<T>> operation) : QueuedRequest(sessionId)
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            try {
                var result = await operation(cancellationToken);

                if (TryComplete()) {
                    _completion.TrySetResult(result);
                }
            }
            catch (Exception ex) {
                TryFail(ex);
            }
        }

        public override void TryFail(Exception exception, bool disposeCancellationRegistration = true)
        {
            if (TryComplete(disposeCancellationRegistration)) {
                _completion.TrySetException(exception);
            }
        }
    }
}
