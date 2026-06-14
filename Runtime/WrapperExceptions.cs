namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Raised when the request queue is full.
/// </summary>
public sealed class QueueFullException : Exception
{
    public QueueFullException(int capacity)
        : base($"The codebase-memory wrapper queue is full ({capacity} pending requests).")
    {
    }
}

/// <summary>
/// Raised when the backend child process exits during an operation.
/// </summary>
public sealed class ChildProcessUnavailableException : Exception
{
    public ChildProcessUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
