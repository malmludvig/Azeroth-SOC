using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// A socket proxy between the game backend and Docker. It forwards exactly the
// calls the lab needs and checks what is inside them, so that whoever controls
// the backend can build and judge labs but cannot use Docker to take over the
// host: no privileged containers, no mounts, no containers outside the lab,
// and a privileged exec only for adding a route inside the lab address pool.
//
//   AZEROTH_PROXY_SOCKET     where to listen (default ~/.azeroth/docker-proxy.sock)
//   AZEROTH_DOCKER_UPSTREAM  the real Docker socket (default /var/run/docker.sock)

var upstreamSocket = Environment.GetEnvironmentVariable("AZEROTH_DOCKER_UPSTREAM") ?? "/var/run/docker.sock";
var listenSocket = Environment.GetEnvironmentVariable("AZEROTH_PROXY_SOCKET")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azeroth", "docker-proxy.sock");

// The socket lives in a directory only its owner can enter.
Directory.CreateDirectory(
    Path.GetDirectoryName(listenSocket)!,
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
File.Delete(listenSocket);

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenUnixSocket(listenSocket);
    kestrel.Limits.MaxRequestBodySize = Policy.MaxBodyBytes;
});
var app = builder.Build();

var upstream = new HttpClient(new SocketsHttpHandler
{
    ConnectCallback = async (_, ct) =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(upstreamSocket), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    },
})
{
    BaseAddress = new Uri("http://docker"),
    Timeout = Timeout.InfiniteTimeSpan,
};

var proxy = new Proxy(upstream);
app.Run(proxy.HandleAsync);

app.Lifetime.ApplicationStarted.Register(() =>
{
    File.SetUnixFileMode(listenSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    Console.WriteLine($"Docker proxy listening on unix://{listenSocket}");
    Console.WriteLine($"Use it with: export DOCKER_HOST=unix://{listenSocket}");
});

app.Run();

internal sealed partial class Proxy(HttpClient upstream)
{
    private const string Api = "/v1.44";
    private readonly ConcurrentDictionary<string, bool> _execs = new();

    public async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = ApiPrefix().Replace(request.Path.Value ?? "", "");
        var method = request.Method;

        try
        {
            JsonObject? body = null;
            // A body may arrive chunked, without a length, so always read it.
            if (method == "POST")
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(context.RequestAborted);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    body = JsonNode.Parse(text) as JsonObject;
                    if (body is null) throw new Denied("the request body is not a JSON object");
                }
            }

            Match match;
            if (method == "GET" && path == "/version")
            {
                await ForwardAsync(context, HttpMethod.Get, "/version");
            }
            else if (method == "GET" && (match = ImageInspect().Match(path)).Success)
            {
                Policy.RequireImage(match.Groups[1].Value);
                await ForwardAsync(context, HttpMethod.Get, path);
            }
            else if (method == "GET" && path is "/networks" or "/containers/json")
            {
                var filters = Policy.RequireLabelFilter(request.Query["filters"].ToString());
                var all = path == "/containers/json" ? "all=true&" : "";
                await ForwardAsync(context, HttpMethod.Get, $"{path}?{all}filters={Uri.EscapeDataString(filters)}");
            }
            else if (method == "POST" && path == "/networks/create")
            {
                Policy.RequireNetwork(body);
                await ForwardAsync(context, HttpMethod.Post, path, body);
            }
            else if (method == "DELETE" && (match = NetworkRef().Match(path)).Success)
            {
                await RequireLabObjectAsync($"/networks/{match.Groups[1].Value}", "Labels", context.RequestAborted);
                await ForwardAsync(context, HttpMethod.Delete, path);
            }
            else if (method == "POST" && path == "/containers/create")
            {
                var name = Policy.RequireName(request.Query["name"].ToString());
                Policy.RequireContainer(body);
                await ForwardAsync(context, HttpMethod.Post, $"{path}?name={name}", body);
            }
            else if (method == "POST" && (match = ContainerStart().Match(path)).Success)
            {
                await RequireLabContainerAsync(match.Groups[1].Value, context.RequestAborted);
                await ForwardAsync(context, HttpMethod.Post, path);
            }
            else if (method == "DELETE" && (match = ContainerRef().Match(path)).Success)
            {
                await RequireLabContainerAsync(match.Groups[1].Value, context.RequestAborted);
                await ForwardAsync(context, HttpMethod.Delete, $"{path}?force=true&v=true");
            }
            else if (method == "POST" && (match = ContainerExec().Match(path)).Success)
            {
                await RequireLabContainerAsync(match.Groups[1].Value, context.RequestAborted);
                Policy.RequireExec(body);
                await ForwardAsync(context, HttpMethod.Post, path, body, created =>
                {
                    if (created?["Id"]?.GetValue<string>() is { } id) _execs[id] = true;
                });
            }
            else if (method == "POST" && (match = ExecStart().Match(path)).Success)
            {
                RequireOwnExec(match.Groups[1].Value);
                Policy.RequireExecStart(body);
                await ForwardAsync(context, HttpMethod.Post, path, body);
            }
            else if (method == "GET" && (match = ExecInspect().Match(path)).Success)
            {
                RequireOwnExec(match.Groups[1].Value);
                await ForwardAsync(context, HttpMethod.Get, path);
            }
            else
            {
                throw new Denied("this call is not on the allow list");
            }
        }
        catch (Exception e) when (e is Denied or JsonException or InvalidOperationException or FormatException)
        {
            var reason = e is Denied ? e.Message : "the request is malformed";
            Console.WriteLine($"DENIED {method} {path}: {reason}");
            if (context.Response.HasStarted) return;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = $"Refused by the lab's Docker proxy: {reason}" });
        }
    }

    private void RequireOwnExec(string id)
    {
        if (!_execs.ContainsKey(id)) throw new Denied("this exec was not created through the proxy");
    }

    private Task RequireLabContainerAsync(string reference, CancellationToken ct) =>
        RequireLabObjectAsync($"/containers/{reference}/json", "Config.Labels", ct);

    // Only objects that carry the lab label may be touched. An object that does
    // not exist passes: Docker answers 404 itself and nothing is revealed.
    private async Task RequireLabObjectAsync(string inspectPath, string labelsAt, CancellationToken ct)
    {
        using var response = await upstream.GetAsync(Api + inspectPath, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw new Denied("the object could not be inspected");

        JsonNode? node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var part in labelsAt.Split('.')) node = node?[part];
        if (node?[Policy.LabLabel]?.GetValue<string>() != "1")
            throw new Denied("the object does not belong to the lab");
    }

    private async Task ForwardAsync(
        HttpContext context, HttpMethod method, string pathAndQuery, JsonObject? body = null,
        Action<JsonNode?>? inspect = null)
    {
        using var request = new HttpRequestMessage(method, Api + pathAndQuery);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await upstream.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();
        if (context.Response.StatusCode is 204 or 304) return;

        if (inspect is not null)
        {
            var text = await response.Content.ReadAsStringAsync(context.RequestAborted);
            if (response.IsSuccessStatusCode) inspect(JsonNode.Parse(text));
            await context.Response.WriteAsync(text, context.RequestAborted);
            return;
        }

        // Exec output arrives as a stream: pass each piece on as it comes.
        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    [GeneratedRegex(@"^/v1\.\d+")]
    private static partial Regex ApiPrefix();

    [GeneratedRegex(@"^/images/([a-z-]+/[a-z]+)/json$")]
    private static partial Regex ImageInspect();

    [GeneratedRegex(@"^/networks/([A-Za-z0-9_.-]+)$")]
    private static partial Regex NetworkRef();

    [GeneratedRegex(@"^/containers/([A-Za-z0-9_.-]+)$")]
    private static partial Regex ContainerRef();

    [GeneratedRegex(@"^/containers/([A-Za-z0-9_.-]+)/start$")]
    private static partial Regex ContainerStart();

    [GeneratedRegex(@"^/containers/([A-Za-z0-9_.-]+)/exec$")]
    private static partial Regex ContainerExec();

    [GeneratedRegex(@"^/exec/([a-f0-9]+)/start$")]
    private static partial Regex ExecStart();

    [GeneratedRegex(@"^/exec/([a-f0-9]+)/json$")]
    private static partial Regex ExecInspect();
}

internal sealed class Denied(string reason) : Exception(reason);
