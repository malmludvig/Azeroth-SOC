namespace Game.Orchestration;

/// <summary>
/// Topology of the first scenario: hosts on the plains, a web server in the
/// DMZ, and the gate routing between them. Subnets, host addresses and the
/// two forgotten ports on the web server are drawn from the seed.
/// </summary>
public static class SentinelHill
{
    public const string Scenario = "sentinel-hill";
    public const string Plains = "plains";
    public const string Dmz = "dmz";

    /// <summary>All orchestrated lab networks are /24s inside this block.</summary>
    public const string AddressPool = "10.77.0.0/16";

    private const long Mb = 1024 * 1024;
    private static readonly int[] DatabasePorts = [3306, 5432, 6379, 27017];
    private static readonly string[] PlainsHosts = ["citizens", "garrison", "defias", "probe"];
    private static readonly string[] Words =
        ["lantern", "harvest", "anvil", "barley", "falcon", "copper", "meadow", "thistle", "granite", "hearth", "willow", "ember"];

    public static LabSpec Create(int seed)
    {
        var rng = new Random(seed);

        var thirdOctets = Distinct(rng, 2, 1, 255);
        var plainsNet = $"10.77.{thirdOctets[0]}";
        var dmzNet = $"10.77.{thirdOctets[1]}";
        var plainsSubnet = plainsNet + ".0/24";
        var dmzSubnet = dmzNet + ".0/24";

        // .1 is left alone: Docker uses it for a bridge gateway in the fallback driver.
        var plainsOctets = Distinct(rng, 1 + PlainsHosts.Length, 10, 251);
        var dmzOctets = Distinct(rng, 2, 10, 251);
        var gateOnPlains = $"{plainsNet}.{plainsOctets[0]}";
        var gateOnDmz = $"{dmzNet}.{dmzOctets[0]}";
        var webAddress = $"{dmzNet}.{dmzOctets[1]}";

        var debugPort = rng.Next(8000, 9000);
        var databasePort = DatabasePorts[rng.Next(DatabasePorts.Length)];
        // Drawn last, so that the networks and ports of a seed stay as they were.
        var word = Words[rng.Next(Words.Length)];

        var none = new Dictionary<string, string>();
        var nodes = new List<NodeSpec>
        {
            new(
                Role: "gate",
                Image: "azeroth-lab/gate",
                Attachments: [new(Plains, gateOnPlains), new(Dmz, gateOnDmz)],
                CapDrop: ["ALL"],
                // NET_ADMIN for nftables and routing, NET_RAW for tcpdump and ping.
                CapAdd: ["NET_ADMIN", "NET_RAW"],
                Sysctls: new Dictionary<string, string> { ["net.ipv4.ip_forward"] = "1" },
                Env: none,
                Routes: [],
                MemoryBytes: 128 * Mb,
                Cpus: 0.5,
                PidsLimit: 128),
            new(
                Role: "web",
                Image: "azeroth-lab/web",
                Attachments: [new(Dmz, webAddress)],
                CapDrop: ["NET_RAW", "MKNOD", "AUDIT_WRITE"],
                CapAdd: [],
                Sysctls: none,
                Env: new Dictionary<string, string>
                {
                    ["DEBUG_PORT"] = debugPort.ToString(),
                    ["DB_PORT"] = databasePort.ToString(),
                    ["BOARD_WORD"] = word,
                },
                Routes: [new(plainsSubnet, gateOnDmz)],
                // Deliberately small so the swarm in wave 3 hurts.
                MemoryBytes: 128 * Mb,
                Cpus: 0.25,
                PidsLimit: 64),
        };

        for (var i = 0; i < PlainsHosts.Length; i++)
        {
            nodes.Add(new(
                Role: PlainsHosts[i],
                Image: "azeroth-lab/host",
                Attachments: [new(Plains, $"{plainsNet}.{plainsOctets[i + 1]}")],
                CapDrop: ["ALL"],
                CapAdd: [],
                Sysctls: none,
                Env: none,
                Routes: [new(dmzSubnet, gateOnPlains)],
                MemoryBytes: 128 * Mb,
                Cpus: 0.5,
                PidsLimit: 128));
        }

        return new LabSpec(
            Scenario,
            seed,
            Networks: [new(Plains, plainsSubnet), new(Dmz, dmzSubnet)],
            Nodes: nodes,
            Health: new HealthProbe("probe", webAddress, 80),
            Parameters: new Dictionary<string, string>
            {
                ["web.http"] = "80",
                ["web.management"] = "22",
                ["web.debug"] = debugPort.ToString(),
                ["web.database"] = databasePort.ToString(),
            });
    }

    private static int[] Distinct(Random rng, int count, int minInclusive, int maxExclusive)
    {
        var picked = new HashSet<int>();
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            int value;
            do value = rng.Next(minInclusive, maxExclusive);
            while (!picked.Add(value));
            result[i] = value;
        }
        return result;
    }
}
