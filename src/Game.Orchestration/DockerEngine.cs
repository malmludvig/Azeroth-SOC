using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Game.Orchestration;

public sealed class DockerException(HttpStatusCode status, string message)
    : Exception($"Docker answered {(int)status}: {message}")
{
    public HttpStatusCode Status { get; } = status;
}

public sealed record ExecResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// A deliberately small client for the Docker Engine API. It exposes only the
/// calls the lab needs, which is also the list a socket proxy has to allow:
/// ping, image inspect, network create/list/remove, container
/// create/start/list/remove, and exec.
/// </summary>
public sealed class DockerEngine : IDisposable
{
    private const string Api = "/v1.44";
    private readonly HttpClient _http;

    /// <param name="host">
    /// unix:///path/to/docker.sock or tcp://host:port. Defaults to DOCKER_HOST,
    /// then to the standard socket.
    /// </param>
    public DockerEngine(string? host = null)
    {
        host ??= Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrWhiteSpace(host)) host = "unix:///var/run/docker.sock";

        if (host.StartsWith("unix://", StringComparison.Ordinal))
        {
            var path = host["unix://".Length..];
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            _http = new HttpClient(handler) { BaseAddress = new Uri("http://docker") };
        }
        else if (host.StartsWith("tcp://", StringComparison.Ordinal))
        {
            _http = new HttpClient { BaseAddress = new Uri("http://" + host["tcp://".Length..]) };
        }
        else
        {
            throw new ArgumentException($"Unsupported Docker host '{host}'. Use unix:// or tcp://.");
        }

        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public void Dispose() => _http.Dispose();

    public async Task<string> ServerVersionAsync(CancellationToken ct = default)
    {
        var version = await GetAsync("/version", ct);
        return version?["Version"]?.GetValue<string>() ?? "unknown";
    }

    public async Task<bool> ImageExistsAsync(string image, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{Api}/images/{image}/json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    public async Task<string> CreateNetworkAsync(
        string name, string driver, string subnet, IReadOnlyDictionary<string, string> labels,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["Name"] = name,
            ["Driver"] = driver,
            ["Internal"] = true,
            ["IPAM"] = new JsonObject
            {
                ["Config"] = new JsonArray(new JsonObject { ["Subnet"] = subnet }),
            },
            ["Labels"] = ToJson(labels),
        };
        var created = await PostAsync("/networks/create", body, ct);
        return created!["Id"]!.GetValue<string>();
    }

    public async Task<IReadOnlyList<JsonNode>> ListNetworksAsync(string label, CancellationToken ct = default) =>
        AsList(await GetAsync("/networks?filters=" + LabelFilter(label), ct));

    public Task RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        DeleteAsync($"/networks/{id}", ct);

    public async Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct = default)
    {
        var created = await PostAsync("/containers/create?name=" + Uri.EscapeDataString(name), body, ct);
        return created!["Id"]!.GetValue<string>();
    }

    public Task StartContainerAsync(string id, CancellationToken ct = default) =>
        PostAsync($"/containers/{id}/start", null, ct);

    public async Task<IReadOnlyList<JsonNode>> ListContainersAsync(string label, CancellationToken ct = default) =>
        AsList(await GetAsync("/containers/json?all=true&filters=" + LabelFilter(label), ct));

    public Task RemoveContainerAsync(string id, CancellationToken ct = default) =>
        DeleteAsync($"/containers/{id}?force=true&v=true", ct);

    /// <summary>
    /// Runs a command in a container and returns its exit code and combined output.
    /// A privileged exec gives that one command full capabilities without the
    /// container itself holding them; the lab uses it only to add routes.
    /// </summary>
    public async Task<ExecResult> ExecAsync(
        string container, IReadOnlyList<string> command, bool privileged = false,
        CancellationToken ct = default)
    {
        var create = new JsonObject
        {
            ["Cmd"] = new JsonArray(command.Select(c => (JsonNode)c).ToArray()),
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Privileged"] = privileged,
            ["User"] = "0",
        };
        var exec = await PostAsync($"/containers/{container}/exec", create, ct);
        var id = exec!["Id"]!.GetValue<string>();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/exec/{id}/start")
        {
            Content = JsonContent.Create(new JsonObject { ["Detach"] = false, ["Tty"] = false }),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var output = await ReadMultiplexedAsync(stream, ct);

        var inspect = await GetAsync($"/exec/{id}/json", ct);
        var exitCode = inspect?["ExitCode"]?.GetValue<int>() ?? -1;
        return new ExecResult(exitCode, output);
    }

    // Without a TTY Docker frames the output: 1 byte stream id, 3 bytes padding,
    // 4 bytes big-endian length, then the payload.
    private static async Task<string> ReadMultiplexedAsync(Stream stream, CancellationToken ct)
    {
        var output = new MemoryStream();
        var header = new byte[8];
        while (await ReadExactlyOrEndAsync(stream, header, ct))
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            var payload = new byte[length];
            if (!await ReadExactlyOrEndAsync(stream, payload, ct)) break;
            output.Write(payload);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Api + path, ct);
        return await ReadAsync(response, ct);
    }

    private async Task<JsonNode?> PostAsync(string path, JsonNode? body, CancellationToken ct)
    {
        using var content = body is null ? null : JsonContent.Create(body);
        using var response = await _http.PostAsync(Api + path, content, ct);
        return await ReadAsync(response, ct);
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync(Api + path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, ct);
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        var message = text;
        try
        {
            message = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text;
        }
        catch (JsonException)
        {
            // Not JSON: keep the raw body.
        }
        throw new DockerException(response.StatusCode, message.Trim());
    }

    private static string LabelFilter(string label) =>
        Uri.EscapeDataString(new JsonObject { ["label"] = new JsonArray(label) }.ToJsonString());

    private static IReadOnlyList<JsonNode> AsList(JsonNode? node) =>
        node is JsonArray array ? array.Where(n => n is not null).Select(n => n!).ToList() : [];

    internal static JsonObject ToJson(IReadOnlyDictionary<string, string> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values) json[key] = value;
        return json;
    }
}
