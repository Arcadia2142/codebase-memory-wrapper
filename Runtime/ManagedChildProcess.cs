using System.Diagnostics;
using System.Runtime.InteropServices;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using CodebaseMemory.Wrapper.Options;

namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Owns one codebase-memory stdio child process and its MCP client.
/// </summary>
public sealed class ManagedChildProcess : IAsyncDisposable
{
    private const int SigTerm = 15;
    private const int SigKill = 9;

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly ChildProcessOptions _options;
    private readonly bool _usesProcessGroup;
    private readonly Task _stderrPump;

    private ManagedChildProcess(
        Process process,
        McpClient client,
        ChildProcessOptions options,
        bool usesProcessGroup,
        Task stderrPump,
        ILogger logger)
    {
        _process = process;
        _options = options;
        _usesProcessGroup = usesProcessGroup;
        _stderrPump = stderrPump;
        _logger = logger;
        Client = client;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// MCP client connected to the child stdio streams.
    /// </summary>
    public McpClient Client { get; }

    /// <summary>
    /// UTC timestamp when the child was started.
    /// </summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Current process id.
    /// </summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// True when the child process has exited.
    /// </summary>
    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Starts a child process and initializes an MCP client over its stdio streams.
    /// </summary>
    public static async Task<ManagedChildProcess> StartAsync(
        ChildProcessOptions options,
        ILogger logger,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var (startInfo, usesProcessGroup) = CreateStartInfo(options);
        var process = Process.Start(startInfo);

        if (process is null) {
            throw new ChildProcessUnavailableException("Failed to start codebase-memory child process.");
        }

        var stderrPump = PumpStandardErrorAsync(process, logger, cancellationToken);
        var transport = new StreamClientTransport(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            loggerFactory);

        try {
            var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
            logger.LogInformation("Started codebase-memory child process pid={Pid}.", process.Id);
            return new ManagedChildProcess(process, client, options, usesProcessGroup, stderrPump, logger);
        }
        catch {
            await StopProcessAsync(process, options.ShutdownGracePeriod, usesProcessGroup, logger);
            throw;
        }
    }

    /// <summary>
    /// Stops the child process and releases the MCP client.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try {
            await Client.DisposeAsync();
        }
        catch (Exception ex) {
            _logger.LogDebug(ex, "Ignoring MCP client dispose error.");
        }

        await StopProcessAsync(_process, _options.ShutdownGracePeriod, _usesProcessGroup, _logger);

        try {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) {
            _logger.LogDebug("Timed out waiting for child stderr pump.");
        }
    }

    private static (ProcessStartInfo StartInfo, bool UsesProcessGroup) CreateStartInfo(ChildProcessOptions options)
    {
        var useProcessGroup = OperatingSystem.IsLinux() &&
            options.UseProcessGroupOnLinux &&
            File.Exists("/usr/bin/setsid");

        var startInfo = new ProcessStartInfo
        {
            FileName = useProcessGroup ? "/usr/bin/setsid" : options.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory)) {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        if (useProcessGroup) {
            startInfo.ArgumentList.Add(options.Command);
        }

        foreach (var argument in options.Arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        return (startInfo, useProcessGroup);
    }

    private static async Task PumpStandardErrorAsync(Process process, ILogger logger, CancellationToken cancellationToken)
    {
        try {
            while (!cancellationToken.IsCancellationRequested) {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);

                if (line is null) {
                    break;
                }

                logger.LogInformation("codebase-memory stderr: {Line}", line);
            }
        }
        catch (OperationCanceledException) {
            // Shutdown path.
        }
        catch (Exception ex) {
            logger.LogDebug(ex, "Child stderr pump stopped.");
        }
    }

    private static async Task StopProcessAsync(
        Process process,
        TimeSpan gracePeriod,
        bool usesProcessGroup,
        ILogger logger)
    {
        if (process.HasExited) {
            return;
        }

        SendSignal(process, usesProcessGroup, SigTerm, logger);

        try {
            await process.WaitForExitAsync().WaitAsync(gracePeriod);
            return;
        }
        catch (TimeoutException) {
            logger.LogWarning("Child process did not exit after SIGTERM; sending SIGKILL.");
        }

        SendSignal(process, usesProcessGroup, SigKill, logger);
        await process.WaitForExitAsync();
    }

    private static void SendSignal(Process process, bool usesProcessGroup, int signal, ILogger logger)
    {
        try {
            if (OperatingSystem.IsLinux()) {
                var target = usesProcessGroup ? -process.Id : process.Id;
                _ = kill(target, signal);
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) {
            logger.LogDebug(ex, "Failed to send signal {Signal} to child pid={Pid}.", signal, process.Id);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
