using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Game.Orchestration;

/// <summary>
/// Tries to get out of the lab from inside every container: to the internet,
/// to this machine and its gateway, and to the Docker socket. Every attempt
/// has to fail. Also checks that capabilities and limits are as small as the
/// design says.
/// </summary>
internal static class IsolationTest
{
    private const long NetAdmin = 1L << 12, NetRaw = 1L << 13, SysModule = 1L << 16, SysRawIo = 1L << 17,
        SysPtrace = 1L << 19, SysAdmin = 1L << 21;

    public static async Task<int> RunAsync(DockerEngine docker, LabOrchestrator lab)
    {
        if ((await lab.ListAsync()).Count > 0)
        {
            Console.Error.WriteLine("A lab is already running. Remove it first: down");
            return 1;
        }

        var outside = OutsideAddresses();
        var run = await lab.UpAsync(SentinelHill.Create(Random.Shared.Next()));
        Console.WriteLine($"Run {run.RunId}  networks {run.Driver}");
        Console.WriteLine($"Addresses of this machine and its gateways: {string.Join(", ", outside)}");
        var failures = 0;

        void Report(string role, string what, bool ok, string detail = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {role,-9} {what}{(detail.Length > 0 ? $" ({detail})" : "")}");
        }

        try
        {
            var mounts = (await docker.ListContainersAsync($"azeroth.run={run.RunId}"))
                .ToDictionary(c => c["Labels"]!["azeroth.role"]!.GetValue<string>(), c => c["Mounts"]?.AsArray().Count ?? 0);

            foreach (var node in run.Spec.Nodes)
            {
                var role = node.Role;
                Task<ExecResult> Sh(string script) => docker.ExecAsync(run.ContainerName(role), ["sh", "-c", script]);

                var internet = await Sh("ip route get 1.1.1.1 >/dev/null 2>&1 || nc -z -w 2 1.1.1.1 443 >/dev/null 2>&1");
                Report(role, "has no way to the internet", !internet.Succeeded);

                var reachable = new List<string>();
                foreach (var address in outside)
                    if ((await Sh($"ip route get {address} >/dev/null 2>&1")).Succeeded) reachable.Add(address);
                Report(role, "has no way to this machine or its gateways", reachable.Count == 0, string.Join(", ", reachable));

                var routes = (await Sh("ip -o route show")).Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var foreign = routes.Where(r => !r.StartsWith("10.77.", StringComparison.Ordinal)).ToList();
                Report(role, "has routes to lab networks only", routes.Length > 0 && foreign.Count == 0, string.Join("; ", foreign));

                // The Docker host must not sit on a lab network. With macvlan it
                // does not; a bridge puts the host on .1 of every subnet.
                var hostSeen = new List<string>();
                foreach (var attachment in node.Attachments)
                {
                    var first = attachment.Address[..attachment.Address.LastIndexOf('.')] + ".1";
                    var neighbour = await Sh($"nc -z -w 1 {first} 80 >/dev/null 2>&1; ip neigh show {first}");
                    if (neighbour.Output.Contains("lladdr", StringComparison.Ordinal)) hostSeen.Add(first);
                }
                Report(role, "does not share a network with the Docker host", hostSeen.Count == 0, string.Join(", ", hostSeen));

                var socket = await Sh("test -e /var/run/docker.sock || test -e /run/docker.sock");
                Report(role, "has no Docker socket and no mounts", !socket.Succeeded && mounts.GetValueOrDefault(role) == 0);

                var status = (await Sh("grep -E 'CapEff|NoNewPrivs' /proc/1/status")).Output;
                var caps = long.Parse(Field(status, "CapEff"), NumberStyles.HexNumber);
                var capsOk = role switch
                {
                    "gate" => caps == (NetAdmin | NetRaw),
                    "web" => (caps & (NetAdmin | NetRaw | SysModule | SysRawIo | SysPtrace | SysAdmin)) == 0,
                    _ => caps == 0,
                };
                var expected = role switch
                {
                    "gate" => "exactly NET_ADMIN and NET_RAW",
                    "web" => "none of the dangerous capabilities",
                    _ => "no capabilities",
                };
                Report(role, $"holds {expected}", capsOk, capsOk ? "" : $"CapEff={caps:x}");
                Report(role, "cannot gain new privileges", Field(status, "NoNewPrivs") == "1");

                var limits = (await Sh(
                    "cat /sys/fs/cgroup/pids.max 2>/dev/null || cat /sys/fs/cgroup/pids/pids.max; " +
                    "cat /sys/fs/cgroup/memory.max 2>/dev/null || cat /sys/fs/cgroup/memory/memory.limit_in_bytes")).Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var limited = limits.Length == 2
                    && long.TryParse(limits[0], out var pids) && pids == node.PidsLimit
                    && long.TryParse(limits[1], out var memory) && memory == node.MemoryBytes;
                Report(role, "is limited in processes and memory", limited, limited ? "" : string.Join(" / ", limits));
            }

            // The player is root in the gate and may add routes. A route out of
            // the lab must still lead nowhere.
            var web = run.Spec.Node("web").AddressOn(SentinelHill.Dmz);
            var escape = await docker.ExecAsync(run.ContainerName("gate"), ["sh", "-c",
                $"ip route add default via {web} && nc -z -w 2 1.1.1.1 443; rc=$?; ip route del default; exit $rc"]);
            Report("gate", "reaches nothing even after adding a default route", !escape.Succeeded);
        }
        finally
        {
            await lab.DownAsync(run.RunId);
        }

        Console.WriteLine(failures == 0 ? "Isolation test passed." : $"Isolation test FAILED: {failures} check(s).");
        return failures == 0 ? 0 : 1;
    }

    private static string Field(string status, string name) =>
        status.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal))?.Split(':')[1].Trim() ?? "";

    /// <summary>IPv4 addresses and gateways of the machine the test runs on, outside the lab pool.</summary>
    private static List<string> OutsideAddresses()
    {
        var addresses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var properties = nic.GetIPProperties();
            foreach (var address in properties.UnicastAddresses.Select(a => a.Address)
                         .Concat(properties.GatewayAddresses.Select(g => g.Address)))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                var text = address.ToString();
                if (text == "0.0.0.0" || text.StartsWith("10.77.", StringComparison.Ordinal)) continue;
                addresses.Add(text);
            }
        }
        return [.. addresses];
    }
}
