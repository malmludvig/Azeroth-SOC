using System.Text.Json.Nodes;

namespace Game.Orchestration;

public sealed record LabNode(string Role, string ContainerId, string ContainerName, string State);

public sealed record LabRun(
    string RunId,
    LabSpec Spec,
    string Driver,
    DateTimeOffset Expires,
    IReadOnlyList<LabNode> Nodes)
{
    public string ContainerName(string role) => LabOrchestrator.ContainerName(RunId, role);

    /// <summary>
    /// Every container of the run exists and is running. Containers do not
    /// restart on their own, so a run is stopped after Docker itself restarts.
    /// </summary>
    public bool IsRunning =>
        Nodes.Count == Spec.Nodes.Count && Nodes.All(n => n.State == "running");
}

public sealed record LabResources(int Containers, int Networks)
{
    public bool IsEmpty => Containers == 0 && Networks == 0;
}

public sealed class LabException(string message) : Exception(message);

/// <summary>
/// Creates, resets and removes lab runs. Every container and network it creates
/// carries the lab label and a run id, and that is the only state: a run can
/// always be found, rebuilt or cleaned up from the labels alone.
/// </summary>
public sealed class LabOrchestrator(DockerEngine docker, Action<string>? log = null)
{
    public const string LabLabel = "azeroth.lab";
    private const string RunLabel = "azeroth.run";
    private const string ScenarioLabel = "azeroth.scenario";
    private const string SeedLabel = "azeroth.seed";
    private const string ExpiresLabel = "azeroth.expires";
    private const string RoleLabel = "azeroth.role";
    private const string DriverLabel = "azeroth.driver";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(4);

    private readonly Action<string> _log = log ?? (_ => { });

    public static string ContainerName(string runId, string role) => $"azl-{runId}-{role}";

    private static string NetworkName(string runId, string key) => $"azl-{runId}-{key}";

    /// <summary>Builds a new run. If anything fails, everything created so far is removed.</summary>
    public async Task<LabRun> UpAsync(LabSpec spec, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        foreach (var image in spec.Nodes.Select(n => n.Image).Distinct())
        {
            if (!await docker.ImageExistsAsync(image, ct))
                throw new LabException($"Image '{image}' is missing. Build the lab images first: lab/build.sh");
        }

        var runId = Guid.NewGuid().ToString("N")[..8];
        var expires = DateTimeOffset.UtcNow + (lifetime ?? DefaultLifetime);
        var labels = new Dictionary<string, string>
        {
            [LabLabel] = "1",
            [RunLabel] = runId,
            [ScenarioLabel] = spec.Scenario,
            [SeedLabel] = spec.Seed.ToString(),
            [ExpiresLabel] = expires.ToUnixTimeSeconds().ToString(),
        };

        try
        {
            var driver = await CreateNetworksAsync(runId, spec, labels, ct);
            labels[DriverLabel] = driver;

            foreach (var node in spec.Nodes)
            {
                var id = await docker.CreateContainerAsync(
                    ContainerName(runId, node.Role), ContainerBody(runId, node, labels), ct);
                await docker.StartContainerAsync(id, ct);
            }

            foreach (var node in spec.Nodes)
            {
                foreach (var route in node.Routes)
                {
                    var result = await docker.ExecAsync(
                        ContainerName(runId, node.Role),
                        ["ip", "route", "replace", route.Destination, "via", route.Via],
                        privileged: true, ct);
                    if (!result.Succeeded)
                        throw new LabException($"Could not add a route in {node.Role}: {result.Output.Trim()}");
                }
            }

            await WaitUntilHealthyAsync(runId, spec, driver, ct);
        }
        catch
        {
            await DownAsync(runId, CancellationToken.None);
            throw;
        }

        return await FindAsync(runId, ct)
            ?? throw new LabException($"Run {runId} disappeared right after it was created.");
    }

    /// <summary>Removes the run and builds it again from the images, with the same seed.</summary>
    public async Task<LabRun> ResetAsync(LabRun run, CancellationToken ct = default)
    {
        await DownAsync(run.RunId, ct);
        return await UpAsync(run.Spec, ct: ct);
    }

    public async Task DownAsync(string runId, CancellationToken ct = default)
    {
        var filter = $"{RunLabel}={runId}";
        foreach (var container in await docker.ListContainersAsync(filter, ct))
            await docker.RemoveContainerAsync(container["Id"]!.GetValue<string>(), ct);

        foreach (var network in await docker.ListNetworksAsync(filter, ct))
            await RemoveNetworkWithRetryAsync(network["Id"]!.GetValue<string>(), ct);
    }

    /// <summary>All runs that currently have containers or networks, newest label data first.</summary>
    public async Task<IReadOnlyList<LabRun>> ListAsync(CancellationToken ct = default)
    {
        var containers = await docker.ListContainersAsync(LabLabel, ct);
        var networks = await docker.ListNetworksAsync(LabLabel, ct);

        var runIds = containers.Select(c => Label(c, RunLabel))
            .Concat(networks.Select(n => Label(n, RunLabel)))
            .Where(id => id is not null)
            .Distinct()
            .ToList();

        var runs = new List<LabRun>();
        foreach (var runId in runIds)
        {
            var mine = containers.Where(c => Label(c, RunLabel) == runId).ToList();
            var source = mine.FirstOrDefault() ?? networks.First(n => Label(n, RunLabel) == runId);

            var scenario = Label(source, ScenarioLabel);
            if (scenario != SentinelHill.Scenario || !int.TryParse(Label(source, SeedLabel), out var seed))
                throw new LabException($"Run {runId} has labels this version does not understand. Remove it with: sweep");

            var expires = long.TryParse(Label(source, ExpiresLabel), out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.MinValue;

            var nodes = mine
                .Select(c => new LabNode(
                    Label(c, RoleLabel) ?? "?",
                    c["Id"]!.GetValue<string>(),
                    c["Names"]?[0]?.GetValue<string>().TrimStart('/') ?? "?",
                    c["State"]?.GetValue<string>() ?? "?"))
                .OrderBy(n => n.Role)
                .ToList();

            runs.Add(new LabRun(runId!, SentinelHill.Create(seed), Label(source, DriverLabel) ?? "?", expires, nodes));
        }
        return runs;
    }

    public async Task<LabRun?> FindAsync(string runId, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(r => r.RunId == runId);

    /// <summary>
    /// Removes lab runs: all of them, or only those past their maximum lifetime.
    /// Works from the labels on the Docker objects, so it also removes runs this
    /// version cannot interpret. Returns the number of runs removed.
    /// </summary>
    public async Task<int> SweepAsync(bool expiredOnly, CancellationToken ct = default)
    {
        var containers = await docker.ListContainersAsync(LabLabel, ct);
        var networks = await docker.ListNetworksAsync(LabLabel, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var runIds = containers.Concat(networks)
            .Where(o => !expiredOnly
                || !long.TryParse(Label(o, ExpiresLabel), out var expires)
                || expires <= now)
            .Select(o => Label(o, RunLabel))
            .Where(id => id is not null)
            .Distinct()
            .ToList();

        foreach (var runId in runIds)
        {
            _log($"Removing run {runId}");
            await DownAsync(runId!, ct);
        }
        return runIds.Count;
    }

    public async Task<LabResources> CountResourcesAsync(CancellationToken ct = default) =>
        new((await docker.ListContainersAsync(LabLabel, ct)).Count,
            (await docker.ListNetworksAsync(LabLabel, ct)).Count);

    // macvlan on a dummy parent is the working choice: on an internal bridge the
    // host firewall drops routed packets when it filters bridged traffic.
    private async Task<string> CreateNetworksAsync(
        string runId, LabSpec spec, Dictionary<string, string> labels, CancellationToken ct)
    {
        var preferred = Environment.GetEnvironmentVariable("LAB_NET_DRIVER");
        if (string.IsNullOrWhiteSpace(preferred)) preferred = "macvlan";

        try
        {
            await CreateNetworksAsync(runId, spec, labels, preferred, ct);
            return preferred;
        }
        catch (DockerException e) when (preferred == "macvlan")
        {
            _log($"macvlan networks failed ({e.Message}). Falling back to bridge networks.");
            foreach (var network in await docker.ListNetworksAsync($"{RunLabel}={runId}", ct))
                await RemoveNetworkWithRetryAsync(network["Id"]!.GetValue<string>(), ct);
            await CreateNetworksAsync(runId, spec, labels, "bridge", ct);
            return "bridge";
        }
    }

    private async Task CreateNetworksAsync(
        string runId, LabSpec spec, Dictionary<string, string> labels, string driver, CancellationToken ct)
    {
        var withDriver = new Dictionary<string, string>(labels) { [DriverLabel] = driver };
        foreach (var network in spec.Networks)
            await docker.CreateNetworkAsync(NetworkName(runId, network.Key), driver, network.Subnet, withDriver, ct);
    }

    private static JsonObject ContainerBody(string runId, NodeSpec node, Dictionary<string, string> labels)
    {
        var nodeLabels = new Dictionary<string, string>(labels) { [RoleLabel] = node.Role };

        var endpoints = new JsonObject();
        foreach (var attachment in node.Attachments)
        {
            endpoints[NetworkName(runId, attachment.NetworkKey)] = new JsonObject
            {
                ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = attachment.Address },
            };
        }

        return new JsonObject
        {
            ["Image"] = node.Image,
            ["Hostname"] = node.Role,
            ["Labels"] = DockerEngine.ToJson(nodeLabels),
            ["Env"] = new JsonArray(node.Env.Select(e => (JsonNode)$"{e.Key}={e.Value}").ToArray()),
            ["HostConfig"] = new JsonObject
            {
                ["CapDrop"] = new JsonArray(node.CapDrop.Select(c => (JsonNode)c).ToArray()),
                ["CapAdd"] = new JsonArray(node.CapAdd.Select(c => (JsonNode)c).ToArray()),
                ["SecurityOpt"] = new JsonArray("no-new-privileges:true"),
                ["Privileged"] = false,
                ["Memory"] = node.MemoryBytes,
                ["NanoCpus"] = (long)(node.Cpus * 1_000_000_000),
                ["PidsLimit"] = node.PidsLimit,
                ["Sysctls"] = DockerEngine.ToJson(node.Sysctls),
                ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
            },
            ["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints },
        };
    }

    private async Task WaitUntilHealthyAsync(string runId, LabSpec spec, string driver, CancellationToken ct)
    {
        var probe = spec.Health;
        var container = ContainerName(runId, probe.FromRole);
        string[] command = ["nc", "-z", "-w", "2", probe.Address, probe.Port.ToString()];

        for (var attempt = 0; attempt < 15; attempt++)
        {
            var result = await docker.ExecAsync(container, command, ct: ct);
            if (result.Succeeded) return;
            await Task.Delay(500, ct);
        }

        var hint = driver == "bridge"
            ? " On bridge networks the Docker host drops routed traffic when it filters bridged packets."
            : "";
        throw new LabException(
            $"{probe.FromRole} cannot reach {probe.Address}:{probe.Port} through the gate.{hint}");
    }

    // A network can stay busy for a moment while its endpoints are torn down.
    private async Task RemoveNetworkWithRetryAsync(string id, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await docker.RemoveNetworkAsync(id, ct);
                return;
            }
            catch (DockerException) when (attempt < 10)
            {
                await Task.Delay(300, ct);
            }
        }
    }

    private static string? Label(JsonNode node, string name) => node["Labels"]?[name]?.GetValue<string>();
}
