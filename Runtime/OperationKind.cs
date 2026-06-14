namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Classifies MCP operations for timeout and retry policy.
/// </summary>
public enum OperationKind
{
    /// <summary>
    /// Operation is read-only and can use the short read deadline.
    /// </summary>
    Read,

    /// <summary>
    /// Operation can modify backend state and should not be retried automatically.
    /// </summary>
    Write,

    /// <summary>
    /// Repository indexing operation with a longer dedicated deadline.
    /// </summary>
    Index
}
