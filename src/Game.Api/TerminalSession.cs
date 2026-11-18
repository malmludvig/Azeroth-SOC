using System.Net.WebSockets;
using System.Text.Json;
using Game.Orchestration;

/// <summary>
/// One shell in the gate, kept alive on the server. Browsers attach to it and
/// detach again; the shell and its recent output stay, so a reload or a dropped
/// connection returns to the same prompt with the same history on screen.
/// </summary>
internal sealed class TerminalSession : IDisposable
{
    private const int ReplayBytes = 64 * 1024;
    private const int MaxColumns = 500, MaxRows = 200;

    private readonly DockerEngine _docker;
    private readonly TerminalConnection _connection;
    private readonly object _gate = new();
    private readonly List<Client> _clients = [];
    private readonly Queue<byte[]> _replay = new();
    private readonly SemaphoreSlim _input = new(1, 1);
    private int _replaySize;

    private TerminalSession(DockerEngine docker, TerminalConnection connection)
    {
        _docker = docker;
        _connection = connection;
        _ = PumpAsync();
    }

    public bool Ended { get; private set; }

    public static async Task<TerminalSession> StartAsync(DockerEngine docker, LabRun run, CancellationToken ct)
    {
        var connection = await docker.OpenTerminalAsync(
            run.ContainerName("gate"), ["bash"], ["TERM=xterm-256color", @"PS1=gate:\w# "], ct);
        return new TerminalSession(docker, connection);
    }

    /// <summary>Serves one browser until it disconnects or the shell ends.</summary>
    public async Task AttachAsync(WebSocket socket, CancellationToken ct)
    {
        var client = new Client(socket);
        byte[] history;
        lock (_gate)
        {
            history = _replay.SelectMany(chunk => chunk).ToArray();
            _clients.Add(client);
        }

        try
        {
            if (history.Length > 0) await client.SendAsync(history, ct);

            var buffer = new byte[8 * 1024];
            while (socket.State == WebSocketState.Open && !Ended)
            {
                var message = await ReceiveAsync(socket, buffer, ct);
                if (message is null) break;

                if (message.Value.Type == WebSocketMessageType.Binary)
                {
                    await _input.WaitAsync(ct);
                    try
                    {
                        await _connection.WriteAsync(message.Value.Data, ct);
                    }
                    finally
                    {
                        _input.Release();
                    }
                }
                else
                {
                    await ControlAsync(message.Value.Data, ct);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException)
        {
            // The browser went away, or the shell did. Either way this client is done.
        }
        finally
        {
            lock (_gate) _clients.Remove(client);
        }
    }

    public void Dispose()
    {
        Ended = true;
        _connection.Dispose();
    }

    // The only control message is the terminal's size: {"type":"resize","cols":120,"rows":30}
    private async Task ControlAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (root.GetProperty("type").GetString() != "resize") return;
            var columns = root.GetProperty("cols").GetInt32();
            var rows = root.GetProperty("rows").GetInt32();
            if (columns is < 1 or > MaxColumns || rows is < 1 or > MaxRows) return;
            await _docker.ResizeTerminalAsync(_connection.ExecId, columns, rows, ct);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or DockerException)
        {
            // A malformed or refused size request is not worth ending the terminal for.
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            byte[]? chunk;
            while ((chunk = await _connection.ReadAsync()) is not null)
            {
                Client[] clients;
                lock (_gate)
                {
                    _replay.Enqueue(chunk);
                    _replaySize += chunk.Length;
                    while (_replaySize > ReplayBytes && _replay.Count > 1) _replaySize -= _replay.Dequeue().Length;
                    clients = [.. _clients];
                }
                foreach (var client in clients) await client.TrySendAsync(chunk);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The connection to Docker is gone: the shell has ended.
        }

        Ended = true;
        Client[] remaining;
        lock (_gate) remaining = [.. _clients];
        foreach (var client in remaining) await client.CloseAsync();
    }

    private static async Task<(WebSocketMessageType Type, ReadOnlyMemory<byte> Data)?> ReceiveAsync(
        WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            length += result.Count;
            if (result.EndOfMessage) return (result.MessageType, buffer.AsMemory(0, length));
            if (length == buffer.Length) return null; // larger than anything a terminal sends
        }
    }

    private sealed class Client(WebSocket socket)
    {
        private readonly SemaphoreSlim _sending = new(1, 1);

        public async Task SendAsync(byte[] data, CancellationToken ct)
        {
            await _sending.WaitAsync(ct);
            try
            {
                await socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
            finally
            {
                _sending.Release();
            }
        }

        public async Task TrySendAsync(byte[] data)
        {
            try
            {
                if (socket.State == WebSocketState.Open) await SendAsync(data, CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
            {
                // This browser is gone; its own loop will notice and detach it.
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "The shell ended.", CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
            {
                // Already gone.
            }
        }
    }
}

/// <summary>Keeps one terminal session per lab run and replaces it when its shell has ended.</summary>
internal sealed class TerminalHub(DockerEngine docker) : IDisposable
{
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly Dictionary<string, TerminalSession> _sessions = [];

    public async Task<TerminalSession> GetAsync(LabRun run, CancellationToken ct)
    {
        await _starting.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(run.RunId, out var existing))
            {
                if (!existing.Ended) return existing;
                existing.Dispose();
            }

            // Sessions of runs that no longer exist would otherwise stay forever.
            foreach (var (runId, session) in _sessions.Where(s => s.Value.Ended).ToList())
            {
                session.Dispose();
                _sessions.Remove(runId);
            }

            var started = await TerminalSession.StartAsync(docker, run, ct);
            _sessions[run.RunId] = started;
            return started;
        }
        finally
        {
            _starting.Release();
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
    }
}
