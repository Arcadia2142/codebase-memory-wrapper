using ModelContextProtocol.Protocol;

namespace CodebaseMemory.Wrapper.Runtime;

/// <summary>
/// Bridges public MCP tool handlers to the serialized backend dispatcher.
/// </summary>
public sealed class McpProxyService(
    SessionRequestDispatcher dispatcher,
    CodebaseMemoryBackend backend,
    ILogger<McpProxyService> logger)
{
    /// <summary>
    /// Proxies child initialization metadata through the request dispatcher.
    /// </summary>
    public Task<BackendSessionMetadata> GetSessionMetadataAsync(
        string sessionId,
        CancellationToken cancellationToken) =>
        dispatcher.EnqueueAsync(sessionId, backend.GetSessionMetadataAsync, cancellationToken);

    /// <summary>
    /// Proxies tools/list through the request dispatcher.
    /// </summary>
    public Task<ListToolsResult> ListToolsAsync(
        string sessionId,
        ListToolsRequestParams request,
        CancellationToken cancellationToken) =>
        dispatcher.EnqueueAsync(sessionId, ct => backend.ListToolsAsync(request, ct), cancellationToken);

    /// <summary>
    /// Proxies prompts/list through the request dispatcher.
    /// </summary>
    public Task<ListPromptsResult> ListPromptsAsync(
        string sessionId,
        ListPromptsRequestParams request,
        CancellationToken cancellationToken) =>
        dispatcher.EnqueueAsync(sessionId, ct => backend.ListPromptsAsync(request, ct), cancellationToken);

    /// <summary>
    /// Proxies prompts/get through the request dispatcher.
    /// </summary>
    public Task<GetPromptResult> GetPromptAsync(
        string sessionId,
        GetPromptRequestParams request,
        CancellationToken cancellationToken) =>
        dispatcher.EnqueueAsync(sessionId, ct => backend.GetPromptAsync(request, ct), cancellationToken);

    /// <summary>
    /// Proxies tools/call through the request dispatcher.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(
        string sessionId,
        CallToolRequestParams request,
        CancellationToken cancellationToken)
    {
        var kind = Classify(request.Name);

        try {
            return await dispatcher.EnqueueAsync(
                sessionId,
                ct => backend.CallToolAsync(request, kind, ct),
                cancellationToken);
        }
        catch (Exception ex) when (ex is QueueFullException or ChildProcessUnavailableException or OperationCanceledException) {
            logger.LogWarning(ex, "Returning MCP tool error for {ToolName}.", request.Name);
            return CreateToolError(ex.Message);
        }
    }

    private static OperationKind Classify(string toolName)
    {
        return toolName switch
        {
            "index_repository" => OperationKind.Index,
            "delete_project" or "ingest_traces" or "manage_adr" or "detect_changes" => OperationKind.Write,
            _ => OperationKind.Read
        };
    }

    private static CallToolResult CreateToolError(string message) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"codebase-memory wrapper error: {message}"
                }
            ]
        };
}
