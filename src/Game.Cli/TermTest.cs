using System.Net;
using System.Net.WebSockets;
using System.Text;
using Game.Orchestration;

/// <summary>
/// The test of the terminal gateway: talks to the running API the way the
/// browser page does and requires a working shell in the gate that survives a
/// dropped connection, follows the window's size, and refuses foreign pages.
/// </summary>
internal static class TermTest
{
    public static async Task<int> RunAsync(LabOrchestrator lab)
    {
        var api = new Uri(Environment.GetEnvironmentVariable("AZEROTH_API_URL") ?? "http://127.0.0.1:5080");
        var terminal = new UriBuilder(api) { Scheme = "ws", Path = "/terminal" }.Uri;
        using var http = new HttpClient { BaseAddress = api };

        try
        {
            await http.GetStringAsync("api/status");
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine($"Nothing is listening on {api}. Start it: dotnet run --project src/Game.Api");
            return 1;
        }

        if ((await lab.ListAsync()).Count > 0)
        {
            Console.Error.WriteLine("A lab is already running. Remove it first: down");
            return 1;
        }

        var failures = 0;
        void Expect(string what, bool ok, string shown = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok && shown.Length > 0) Console.WriteLine("        got: " + shown.Trim().Replace("\n", "\n             "));
        }

        var page = await http.GetStringAsync("/");
        Expect("the page is served and loads the terminal", page.Contains("vendor/xterm.js"));
        Expect("the terminal library is served", (await http.GetAsync("vendor/xterm.js")).IsSuccessStatusCode);

        var refusedWithoutLab = await ConnectAsync(terminal) is null;
        Expect("without a lab there is no terminal", refusedWithoutLab);

        var run = await lab.UpAsync(SentinelHill.Create(Random.Shared.Next()));
        try
        {
            var status = await http.GetStringAsync("api/status");
            Expect("the status names the run", status.Contains(run.RunId), status);

            var socket = await ConnectAsync(terminal);
            if (socket is null)
            {
                Expect("a terminal opens", false);
                return 1;
            }

            // A stopped run, as after Docker restarts, gives a clear answer instead of a terminal.
            var stopped = await ProbeStoppedAsync(lab, run, http, terminal);
            Expect("a stopped lab is reported as stopped and opens no terminal", stopped.Ok, stopped.Shown);
            if (!stopped.Ok) return 1;
            run = (await lab.FindAsync(run.RunId))!;
            socket.Dispose();
            socket = await ConnectAsync(terminal);
            if (socket is null)
            {
                Expect("after a restart the terminal opens again", false);
                return 1;
            }

            await TypeAsync(socket, "echo marker-$((6*7))\r");
            var text = await ReadUntilAsync(socket, "marker-42");
            Expect("a command typed in the terminal runs in the gate", text.Contains("marker-42"), text);

            await TypeAsync(socket, "hostname; tty\r");
            text = await ReadUntilAsync(socket, "/dev/pts/");
            Expect("the shell is in the gate and has a real terminal", text.Contains("gate") && text.Contains("/dev/pts/"), text);

            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"resize\",\"cols\":97,\"rows\":31}"),
                WebSocketMessageType.Text, true, CancellationToken.None);
            await Task.Delay(300);
            await TypeAsync(socket, "stty size\r");
            text = await ReadUntilAsync(socket, "31 97");
            Expect("the shell follows the window's size", text.Contains("31 97"), text);

            await TypeAsync(socket, "quest\r");
            text = await ReadUntilAsync(socket, "No game is running.");
            Expect("quest is available in the terminal", text.Contains("No game is running."), text);

            await TypeAsync(socket, "KEPT=yes\r");
            await Task.Delay(300);
            socket.Abort();
            socket.Dispose();

            socket = await ConnectAsync(terminal);
            text = socket is null ? "" : await ReadUntilAsync(socket, "marker-42");
            Expect("after a dropped connection the earlier output is shown again", text.Contains("marker-42"), text);
            if (socket is not null)
            {
                await TypeAsync(socket, "echo state-$KEPT-\r");
                text = await ReadUntilAsync(socket, "state-yes-");
                Expect("and it is the same shell as before", text.Contains("state-yes-"), text);

                await TypeAsync(socket, "exit\r");
                var closed = await ClosesAsync(socket);
                Expect("leaving the shell closes the terminal", closed);
                socket.Dispose();
            }

            socket = await ConnectAsync(terminal);
            if (socket is not null)
            {
                await TypeAsync(socket, "echo state-$KEPT-\r");
                text = await ReadUntilAsync(socket, "state--");
                Expect("connecting again gives a fresh shell", text.Contains("state--"), text);
                socket.Abort();
                socket.Dispose();
            }
            else
            {
                Expect("connecting again gives a fresh shell", false);
            }

            var foreign = await ConnectAsync(terminal, origin: "http://elsewhere.example");
            Expect("a page from another site cannot open the terminal", foreign is null);
            foreign?.Dispose();
        }
        finally
        {
            await lab.DownAsync(run.RunId);
        }

        Console.WriteLine(failures == 0 ? "Terminal test passed." : $"Terminal test FAILED: {failures} check(s).");
        return failures == 0 ? 0 : 1;
    }

    // Stops the gate the way a Docker restart would, checks what the API says,
    // then rebuilds the run under the same id by starting the container again.
    private static async Task<(bool Ok, string Shown)> ProbeStoppedAsync(
        LabOrchestrator lab, LabRun run, HttpClient http, Uri terminal)
    {
        // Stopping a container is not one of the lab's own Docker calls, so the
        // test uses the docker command for it rather than widening DockerEngine.
        var gate = run.ContainerName("gate");
        await DockerCliAsync("stop", "-t", "1", gate);
        try
        {
            var status = await http.GetStringAsync("api/status");
            var listed = await lab.FindAsync(run.RunId);
            var refused = await ConnectAsync(terminal) is null;
            var ok = status.Contains("\"stopped\":true") && status.Contains("\"running\":false")
                && listed is { IsRunning: false } && refused;
            return (ok, status);
        }
        finally
        {
            await DockerCliAsync("start", gate);
        }
    }

    private static async Task DockerCliAsync(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new LabException($"docker {string.Join(' ', arguments)} failed: {await process.StandardError.ReadToEndAsync()}");
    }

    private static async Task<ClientWebSocket?> ConnectAsync(Uri terminal, string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(terminal, timeout.Token);
            return socket;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static Task TypeAsync(ClientWebSocket socket, string keys) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(keys), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<string> ReadUntilAsync(ClientWebSocket socket, string needle)
    {
        var seen = new StringBuilder();
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!seen.ToString().Contains(needle))
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                seen.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // Return what was seen; the caller's check reports the miss.
        }
        return seen.ToString();
    }

    private static async Task<bool> ClosesAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return true;
            }
        }
        catch (WebSocketException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
