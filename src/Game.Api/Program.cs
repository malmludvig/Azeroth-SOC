using Game.Orchestration;

// The game's web backend. For now it does one thing: it serves a page with a
// terminal and connects that terminal to a shell in the gate of the running lab.
//
//   AZEROTH_API_URL  where to listen (default http://127.0.0.1:5080)
//   DOCKER_HOST      Docker, or the lab's Docker proxy
//
// It listens on localhost only. The terminal is a root shell in the gate, so
// nothing but the player's own browser should be able to reach it.

var url = Environment.GetEnvironmentVariable("AZEROTH_API_URL") ?? "http://127.0.0.1:5080";

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ContentRoot(),
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(url);
builder.Services.AddSingleton(_ => new DockerEngine());
builder.Services.AddSingleton<TerminalHub>();
builder.Services.AddSingleton(services => new LabOrchestrator(services.GetRequiredService<DockerEngine>()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/api/status", async (LabOrchestrator lab, CancellationToken ct) =>
{
    try
    {
        var run = (await lab.ListAsync(ct)).FirstOrDefault();
        return Results.Json(new
        {
            running = run?.IsRunning == true,
            stopped = run is not null && !run.IsRunning,
            runId = run?.RunId,
            scenario = run?.Spec.Scenario,
        });
    }
    catch (Exception e) when (e is HttpRequestException or DockerException or LabException)
    {
        return Results.Json(new { running = false, error = e.Message });
    }
});

app.Map("/terminal", async (HttpContext context, LabOrchestrator lab, TerminalHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // A browser sends cookies and reaches localhost for any page that asks it
    // to, so a page from somewhere else must not be able to open this shell.
    if (!SameOrigin(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var run = (await lab.ListAsync(context.RequestAborted)).FirstOrDefault();
    if (run is null || !run.IsRunning)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsync(run is null ? "No lab is running." : "The lab is stopped.");
        return;
    }

    TerminalSession session;
    try
    {
        session = await hub.GetAsync(run, context.RequestAborted);
    }
    catch (DockerException e)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync($"The terminal could not be started: {e.Message}");
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await session.AttachAsync(socket, context.RequestAborted);
});

app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"Open {url} in your browser."));
app.Run();

static bool SameOrigin(HttpRequest request)
{
    var origin = request.Headers.Origin.ToString();
    if (origin.Length == 0) return true; // not a browser: a browser always sends Origin on a WebSocket
    return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
}

// wwwroot sits beside the project file, wherever the program is started from.
static string ContentRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "wwwroot"))) return dir.FullName;
    }
    return AppContext.BaseDirectory;
}
