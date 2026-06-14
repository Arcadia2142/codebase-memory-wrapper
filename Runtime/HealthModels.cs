namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Public health payload returned by /healthz.
/// </summary>
public sealed class WrapperHealth
{
    /// <summary>
    /// True when the wrapper can accept work.
    /// </summary>
    public bool Healthy { get; set; }

    /// <summary>
    /// Text status of the child process lifecycle.
    /// </summary>
    public string ChildState { get; set; } = "idle";

    /// <summary>
    /// Current child process id, or null when the child is idle/stopped.
    /// </summary>
    public int? ChildProcessId { get; set; }

    /// <summary>
    /// Current number of queued requests.
    /// </summary>
    public int QueuedRequests { get; set; }

    /// <summary>
    /// Number of requests currently executing against the child.
    /// </summary>
    public int ActiveRequests { get; set; }

    /// <summary>
    /// Number of sessions with pending queued work.
    /// </summary>
    public int PendingSessions { get; set; }

    /// <summary>
    /// Total child crashes observed during the current wrapper lifetime.
    /// </summary>
    public long TotalCrashes { get; set; }

    /// <summary>
    /// Recent child crashes inside the configured crash-loop window.
    /// </summary>
    public int RecentCrashes { get; set; }

    /// <summary>
    /// True when recent child crashes crossed the unhealthy threshold.
    /// </summary>
    public bool CrashLoop { get; set; }
}
