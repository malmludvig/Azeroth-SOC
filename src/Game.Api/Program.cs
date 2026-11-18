using Game.Api.Components;
using Game.Core;
using Game.Orchestration;

// The game's web backend: the game screen (Blazor Server), the game master
// behind it, and the terminal into the gate.
//
//   AZEROTH_API_URL  where to listen (default http://127.0.0.1:5080)
//   DOCKER_HOST      Docker, or the lab's Docker proxy
//   --start          begin a game right away (--wave N to start at a later wave)
//   AZEROTH_THEME    the theme, a file themes/theme-<name>.json (default wow; --theme <name> overrides)
//   AZEROTH_HOME     where the history is kept (default ~/.azeroth)
//
// It listens on localhost only. The terminal is a root shell in the gate, so
// nothing but the player's own browser should be able to reach it.

var url = Environment.GetEnvironmentVariable("AZEROTH_API_URL") ?? "http://127.0.0.1:5080";

Theme theme;
try
{
    var themeAt = Array.IndexOf(args, "--theme");
    theme = Theme.Current(themeAt >= 0 && themeAt + 1 < args.Length ? args[themeAt + 1] : null);
}
catch (ScenarioException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine($"Themes: {string.Join(", ", Theme.Available())}");
    return 1;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ContentRoot(),
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// Keys for Blazor's circuits stay on this machine; the warning about storing them unencrypted is noise here.
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
builder.WebHost.UseUrls(url);
// Serve static web assets (blazor.web.js from its package) when run from the
// build output in any environment, not only in Development.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(_ => new DockerEngine());
builder.Services.AddSingleton(services => new LabOrchestrator(services.GetRequiredService<DockerEngine>()));
builder.Services.AddSingleton(theme);
builder.Services.AddSingleton(_ => HistoryStore.Default());
builder.Services.AddSingleton<GameHost>();
builder.Services.AddSingleton<TerminalHub>();

var app = builder.Build();

// A browser reaches localhost for any page that asks it to. The game's live
// connections (Blazor's and the terminal's) only accept the game's own page.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if ((path.StartsWithSegments("/_blazor") || path.StartsWithSegments("/terminal")) && !SameOrigin(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseAntiforgery();
// Plain static files: MapStaticAssets answered compressed requests with empty
// bodies when the app ran from the build output instead of a publish.
app.UseStaticFiles();

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

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Open {url} in your browser. Theme: {theme.Name}.");
    // --start begins a game right away, as if Start had been pressed; --wave N picks the first wave.
    if (!args.Contains("--start")) return;
    var at = Array.IndexOf(args, "--wave");
    var wave = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var n) ? n : 0;
    _ = app.Services.GetRequiredService<GameHost>().StartAsync(wave);
});
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<GameHost>().StopAsync().GetAwaiter().GetResult());
app.Run();
return 0;

static bool SameOrigin(HttpRequest request)
{
    var origin = request.Headers.Origin.ToString();
    if (origin.Length == 0) return true; // not a browser: a browser always sends Origin on these requests
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
