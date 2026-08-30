namespace CodebaseMemory.Wrapper.Options;

/// <summary>
/// Root configuration for the local codebase-memory MCP wrapper.
/// </summary>
public sealed class WrapperOptions
{
    /// <summary>
    /// HTTP URL Kestrel binds to.
    /// </summary>
    public string BindUrl { get; set; } = "http://127.0.0.1:39749";

    /// <summary>
    /// Maximum accepted HTTP request body size in bytes.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum number of queued MCP requests waiting for the single backend child.
    /// </summary>
    public int QueueCapacity { get; set; } = 128;

    /// <summary>
    /// Stateful MCP session settings for the public HTTP transport.
    /// </summary>
    public McpSessionOptions Sessions { get; set; } = new();

    /// <summary>
    /// Child process lifecycle settings.
    /// </summary>
    public ChildProcessOptions Child { get; set; } = new();

    /// <summary>
    /// Per-operation deadline settings.
    /// </summary>
    public TimeoutOptions Timeouts { get; set; } = new();

    /// <summary>
    /// Retry and crash-loop policy settings.
    /// </summary>
    public RetryOptions Retry { get; set; } = new();
}

/// <summary>
/// Configures stateful MCP session retention.
/// </summary>
public sealed class McpSessionOptions
{
    /// <summary>
    /// Idle timeout for public MCP sessions.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Maximum number of idle public MCP sessions held by the SDK.
    /// </summary>
    public int MaxIdleSessionCount { get; set; } = 128;
}

/// <summary>
/// Configures the wrapped codebase-memory stdio process.
/// </summary>
public sealed class ChildProcessOptions
{
    /// <summary>
    /// Required path to the codebase-memory-mcp executable.
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Arguments passed to the child command.
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>
    /// Optional working directory for the child command.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Time without work after which the lazy child process is stopped.
    /// </summary>
    public TimeSpan IdleStopAfter { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Grace period between SIGTERM and SIGKILL during child shutdown.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts the child through setsid on Linux so shutdown can target the whole process group.
    /// </summary>
    public bool UseProcessGroupOnLinux { get; set; } = true;
}

/// <summary>
/// Configures deadlines for backend calls.
/// </summary>
public sealed class TimeoutOptions
{
    /// <summary>
    /// Total deadline for read-only tool calls, including retries and backoff.
    /// </summary>
    public TimeSpan Read { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Total deadline for regular write tool calls.
    /// </summary>
    public TimeSpan Write { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Total deadline for index_repository.
    /// </summary>
    public TimeSpan Index { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Configures retry, backoff, and crash-loop detection.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>
    /// Maximum automatic retries after child failure for allowlisted operations.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Operation names that are safe to retry after a child crash.
    /// </summary>
    public List<string> ReadOnlyRetryTools { get; set; } =
    [
        "initialize",
        "tools/list",
        "prompts/list",
        "prompts/get",
        "search_graph",
        "get_code_snippet",
        "get_architecture",
        "trace_path",
        "index_status",
        "list_projects",
        "get_graph_schema",
        "search_code",
        "check_index_coverage",
        "query_graph"
    ];

    /// <summary>
    /// Time a child must stay alive before the restart backoff resets.
    /// </summary>
    public TimeSpan StableRunResetAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sliding window used for crash-loop health classification.
    /// </summary>
    public TimeSpan CrashLoopWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Number of crashes inside CrashLoopWindow that marks the backend unhealthy.
    /// </summary>
    public int CrashLoopThreshold { get; set; } = 3;
}
