namespace Game.Orchestration;

/// <summary>A lab network. Key is the role of the network, not its Docker name.</summary>
public sealed record NetworkSpec(string Key, string Subnet);

public sealed record Attachment(string NetworkKey, string Address);

public sealed record RouteSpec(string Destination, string Via);

public sealed record NodeSpec(
    string Role,
    string Image,
    IReadOnlyList<Attachment> Attachments,
    IReadOnlyList<string> CapDrop,
    IReadOnlyList<string> CapAdd,
    IReadOnlyDictionary<string, string> Sysctls,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<RouteSpec> Routes,
    long MemoryBytes,
    double Cpus,
    int PidsLimit)
{
    public string AddressOn(string networkKey) =>
        Attachments.First(a => a.NetworkKey == networkKey).Address;
}

/// <summary>The lab counts as up when this connection succeeds.</summary>
public sealed record HealthProbe(string FromRole, string Address, int Port);

/// <summary>
/// Everything needed to build one run of a scenario. It is a pure function of
/// the scenario and the seed, so a run can be rebuilt from its labels.
/// </summary>
public sealed record LabSpec(
    string Scenario,
    int Seed,
    IReadOnlyList<NetworkSpec> Networks,
    IReadOnlyList<NodeSpec> Nodes,
    HealthProbe Health,
    IReadOnlyDictionary<string, string> Parameters)
{
    public NodeSpec Node(string role) => Nodes.First(n => n.Role == role);
}
