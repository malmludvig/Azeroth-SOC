using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Game.Orchestration;

/// <summary>
/// The test of the Docker proxy. It sends the requests an intruder in the
/// backend would try and requires every one to be refused, then builds and
/// removes a whole lab through the proxy to show that the lab's own calls pass.
/// </summary>
internal static class ProxyTest
{
    public static string DefaultSocket =>
        Environment.GetEnvironmentVariable("AZEROTH_PROXY_SOCKET")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azeroth", "docker-proxy.sock");

    public static async Task<int> RunAsync()
    {
        var socketPath = DefaultSocket;
        using var http = Client(socketPath);
        using var throughProxy = new DockerEngine("unix://" + socketPath);
        var lab = new LabOrchestrator(throughProxy);

        try
        {
            await throughProxy.ServerVersionAsync();
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine($"No proxy is listening on {socketPath}. Start it: dotnet run --project src/Game.DockerProxy");
            return 1;
        }

        if ((await lab.ListAsync()).Count > 0)
        {
            Console.Error.WriteLine("A lab is already running. Remove it first: down");
            return 1;
        }

        var failures = 0;
        async Task Refused(string what, HttpMethod method, string path, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, "/v1.44" + path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request);
            var ok = response.StatusCode == HttpStatusCode.Forbidden;
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  refused: {what}{(ok ? "" : $" (got {(int)response.StatusCode})")}");
        }

        Console.WriteLine("Requests that must be refused");
        await Refused("list all containers", HttpMethod.Get, "/containers/json");
        await Refused("list containers by a foreign label", HttpMethod.Get,
            "/containers/json?filters=" + Uri.EscapeDataString("{\"label\":[\"com.docker.compose.project\"]}"));
        await Refused("list images", HttpMethod.Get, "/images/json");
        await Refused("inspect a foreign image", HttpMethod.Get, "/images/library/alpine/json");
        await Refused("read Docker's system information", HttpMethod.Get, "/info");

        const string name = "azl-00000000-intruder";
        const string create = "/containers/create?name=" + name;
        await Refused("privileged container", HttpMethod.Post, create, Container(h => h["Privileged"] = true));
        await Refused("container with the Docker socket mounted", HttpMethod.Post, create,
            Container(h => h["Binds"] = new JsonArray("/var/run/docker.sock:/var/run/docker.sock")));
        await Refused("container on the host network", HttpMethod.Post, create, Container(h => h["NetworkMode"] = "host"));
        await Refused("container in the host process namespace", HttpMethod.Post, create, Container(h => h["PidMode"] = "host"));
        await Refused("container with SYS_ADMIN", HttpMethod.Post, create,
            Container(h => h["CapAdd"] = new JsonArray("SYS_ADMIN")));
        await Refused("container without no-new-privileges", HttpMethod.Post, create, Container(h => h.Remove("SecurityOpt")));
        await Refused("container without limits", HttpMethod.Post, create, Container(h => h.Remove("Memory")));
        await Refused("container from a foreign image", HttpMethod.Post, create, Container(_ => { }, image: "alpine"));
        await Refused("container without the lab label", HttpMethod.Post, create, Container(_ => { }, labelled: false));
        await Refused("container with a foreign name", HttpMethod.Post, "/containers/create?name=intruder", Container(_ => { }));
        await Refused("container with an address outside the pool", HttpMethod.Post, create,
            Container(_ => { }, address: "192.168.1.50"));

        await Refused("network that is not internal", HttpMethod.Post, "/networks/create", Network(n => n["Internal"] = false));
        await Refused("network outside the address pool", HttpMethod.Post, "/networks/create",
            Network(n => n["IPAM"] = new JsonObject { ["Config"] = new JsonArray(new JsonObject { ["Subnet"] = "192.168.1.0/24" }) }));
        await Refused("network with driver options", HttpMethod.Post, "/networks/create",
            Network(n => n["Options"] = new JsonObject { ["parent"] = "eth0" }));
        await Refused("network on the host driver", HttpMethod.Post, "/networks/create", Network(n => n["Driver"] = "host"));

        // The rest needs a real lab container to aim at.
        var run = await lab.UpAsync(SentinelHill.Create(Random.Shared.Next()));
        try
        {
            var gate = run.ContainerName("gate");
            await Refused("privileged shell in a lab container", HttpMethod.Post, $"/containers/{gate}/exec",
                Exec(privileged: true, "sh", "-c", "id"));
            await Refused("privileged route outside the pool", HttpMethod.Post, $"/containers/{gate}/exec",
                Exec(privileged: true, "ip", "route", "replace", "0.0.0.0/0", "via", "10.77.1.1"));
            await Refused("exec with a working directory set", HttpMethod.Post, $"/containers/{gate}/exec",
                Exec(privileged: false, "id").With("WorkingDir", "/"));
            await Refused("start an exec the proxy did not create", HttpMethod.Post, "/exec/0123456789abcdef/start",
                new JsonObject { ["Detach"] = false, ["Tty"] = false });
            await Refused("inspect a lab container", HttpMethod.Get, $"/containers/{gate}/json");
            await Refused("copy files out of a lab container", HttpMethod.Get, $"/containers/{gate}/archive?path=/etc");
            await Refused("kill a lab container", HttpMethod.Post, $"/containers/{gate}/kill");
            await Refused("change a lab container's limits", HttpMethod.Post, $"/containers/{gate}/update",
                new JsonObject { ["Memory"] = 0 });

            failures += await OutsiderAsync(Refused);
        }
        finally
        {
            await lab.DownAsync(run.RunId);
        }

        Console.WriteLine("The lab's own calls");
        var left = await lab.CountResourcesAsync();
        var allowed = run.Nodes.Count == run.Spec.Nodes.Count && run.Nodes.All(n => n.State == "running") && left.IsEmpty;
        if (!allowed) failures++;
        Console.WriteLine($"  {(allowed ? "PASS" : "FAIL")}  a lab was built, probed and removed through the proxy");

        Console.WriteLine(failures == 0 ? "Proxy test passed." : $"Proxy test FAILED: {failures} check(s).");
        return failures == 0 ? 0 : 1;
    }

    // Something on the host that is not part of the lab must be out of reach.
    // Creating it needs the real socket; without access to that, this part is skipped.
    private static async Task<int> OutsiderAsync(Func<string, HttpMethod, string, JsonObject?, Task> refused)
    {
        using var direct = new DockerEngine("unix:///var/run/docker.sock");
        string id;
        try
        {
            id = await direct.CreateNetworkAsync(
                "azeroth-proxytest-outsider", "bridge", "10.78.254.0/24",
                new Dictionary<string, string> { ["purpose"] = "proxy test" });
        }
        catch (Exception e) when (e is HttpRequestException or DockerException)
        {
            Console.WriteLine($"  SKIP  remove a network outside the lab (no direct Docker access: {e.Message})");
            return 0;
        }

        try
        {
            await refused("remove a network outside the lab", HttpMethod.Delete, $"/networks/{id}", null);
            var survived = (await direct.ListNetworksAsync("purpose=proxy test")).Count == 1;
            Console.WriteLine($"  {(survived ? "PASS" : "FAIL")}  the outside network is still there");
            return survived ? 0 : 1;
        }
        finally
        {
            await direct.RemoveNetworkAsync(id);
        }
    }

    private static JsonObject Container(
        Action<JsonObject> change, string image = "azeroth-lab/host", bool labelled = true, string address = "10.77.1.50")
    {
        var host = new JsonObject
        {
            ["CapDrop"] = new JsonArray("ALL"),
            ["CapAdd"] = new JsonArray(),
            ["SecurityOpt"] = new JsonArray("no-new-privileges:true"),
            ["Privileged"] = false,
            ["Memory"] = 64L * 1024 * 1024,
            ["NanoCpus"] = 500_000_000L,
            ["PidsLimit"] = 64,
            ["Sysctls"] = new JsonObject(),
            ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
        };
        change(host);
        return new JsonObject
        {
            ["Image"] = image,
            ["Hostname"] = "intruder",
            ["Labels"] = labelled ? new JsonObject { ["azeroth.lab"] = "1" } : new JsonObject(),
            ["Env"] = new JsonArray(),
            ["HostConfig"] = host,
            ["NetworkingConfig"] = new JsonObject
            {
                ["EndpointsConfig"] = new JsonObject
                {
                    ["azl-00000000-plains"] = new JsonObject
                    {
                        ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = address },
                    },
                },
            },
        };
    }

    private static JsonObject Network(Action<JsonObject> change)
    {
        var network = new JsonObject
        {
            ["Name"] = "azl-00000000-intruder",
            ["Driver"] = "bridge",
            ["Internal"] = true,
            ["IPAM"] = new JsonObject { ["Config"] = new JsonArray(new JsonObject { ["Subnet"] = "10.77.1.0/24" }) },
            ["Labels"] = new JsonObject { ["azeroth.lab"] = "1" },
        };
        change(network);
        return network;
    }

    private static JsonObject Exec(bool privileged, params string[] command) => new()
    {
        ["Cmd"] = new JsonArray(command.Select(c => (JsonNode)c).ToArray()),
        ["AttachStdout"] = true,
        ["AttachStderr"] = true,
        ["Privileged"] = privileged,
        ["User"] = "0",
    };

    private static JsonObject With(this JsonObject json, string key, JsonNode value)
    {
        json[key] = value;
        return json;
    }

    private static HttpClient Client(string socketPath) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
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
    };
}
