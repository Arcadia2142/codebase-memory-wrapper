using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CodebaseMemory.Wrapper.Options;
using CodebaseMemory.Wrapper.Runtime;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.Configure<WrapperOptions>(builder.Configuration.GetSection("Wrapper"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionRequestDispatcher>();
builder.Services.AddSingleton<CodebaseMemoryBackend>();
builder.Services.AddSingleton<McpProxyService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionRequestDispatcher>());
builder.Services.AddHostedService<BackendIdleStopService>();

var wrapperOptions = builder.Configuration.GetSection("Wrapper").Get<WrapperOptions>() ?? new WrapperOptions();
ValidateOptions(wrapperOptions);

builder.WebHost.UseUrls(wrapperOptions.BindUrl);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = wrapperOptions.MaxRequestBodyBytes;
});

builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = false;
        options.IdleTimeout = wrapperOptions.Sessions.IdleTimeout;
        options.MaxIdleSessionCount = wrapperOptions.Sessions.MaxIdleSessionCount;
        options.ConfigureSessionOptions = async (httpContext, sessionOptions, cancellationToken) =>
        {
            var proxy = httpContext.RequestServices.GetRequiredService<McpProxyService>();
            var metadata = await proxy.GetSessionMetadataAsync(
                $"initialize:{Guid.NewGuid():N}",
                cancellationToken);

            sessionOptions.ServerInstructions = metadata.ServerInstructions;
            sessionOptions.Capabilities ??= new ServerCapabilities();
            sessionOptions.Capabilities.Tools = metadata.Tools;
            sessionOptions.Capabilities.Prompts = metadata.Prompts;
        };
    })
    .WithListToolsHandler(async (request, cancellationToken) =>
    {
        var proxy = request.Services!.GetRequiredService<McpProxyService>();
        return await proxy.ListToolsAsync(
            GetSessionId(request),
            request.Params ?? new ListToolsRequestParams(),
            cancellationToken);
    })
    .WithCallToolHandler(async (request, cancellationToken) =>
    {
        var proxy = request.Services!.GetRequiredService<McpProxyService>();
        return await proxy.CallToolAsync(GetSessionId(request), request.Params!, cancellationToken);
    })
    .WithListPromptsHandler(async (request, cancellationToken) =>
    {
        var proxy = request.Services!.GetRequiredService<McpProxyService>();
        return await proxy.ListPromptsAsync(
            GetSessionId(request),
            request.Params ?? new ListPromptsRequestParams(),
            cancellationToken);
    })
    .WithGetPromptHandler(async (request, cancellationToken) =>
    {
        var proxy = request.Services!.GetRequiredService<McpProxyService>();
        return await proxy.GetPromptAsync(GetSessionId(request), request.Params!, cancellationToken);
    });

var app = builder.Build();

app.MapGet("/healthz", (
    CodebaseMemoryBackend backend,
    SessionRequestDispatcher dispatcher) =>
{
    var backendHealth = backend.GetHealth();
    var queue = dispatcher.GetSnapshot();
    var health = new WrapperHealth
    {
        Healthy = !backendHealth.CrashLoop,
        ChildState = backendHealth.ChildState,
        ChildProcessId = backendHealth.ChildProcessId,
        QueuedRequests = queue.Pending,
        ActiveRequests = queue.Active,
        PendingSessions = queue.Sessions,
        TotalCrashes = backendHealth.TotalCrashes,
        RecentCrashes = backendHealth.RecentCrashes,
        CrashLoop = backendHealth.CrashLoop
    };

    return backendHealth.CrashLoop
        ? Results.Json(health, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(health);
});

app.MapMcp("/mcp");
await app.RunAsync();

static string GetSessionId<TParams>(RequestContext<TParams> request)
{
    return request.Server.SessionId ?? "unknown-session";
}

static void ValidateOptions(WrapperOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Child.Command)) {
        throw new InvalidOperationException("Wrapper:Child:Command is required.");
    }

    if (options.QueueCapacity <= 0) {
        throw new InvalidOperationException("Wrapper:QueueCapacity must be greater than zero.");
    }

    if (options.MaxRequestBodyBytes <= 0) {
        throw new InvalidOperationException("Wrapper:MaxRequestBodyBytes must be greater than zero.");
    }
}
