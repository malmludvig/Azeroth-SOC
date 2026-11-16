using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// What a request may contain. Every rule is an allow list: a key, value or
/// call that is not named here is refused, including ones Docker adds later.
/// </summary>
internal static partial class Policy
{
    public const string LabLabel = "azeroth.lab";
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly string[] Images = ["azeroth-lab/gate", "azeroth-lab/host", "azeroth-lab/web"];
    private static readonly string[] Drivers = ["macvlan", "bridge"];
    private static readonly string[] AddableCapabilities = ["NET_ADMIN", "NET_RAW"];
    private static readonly string[] Sysctls = ["net.ipv4.ip_forward"];

    public static void RequireImage(string image) =>
        Require(Images.Contains(image), "the image is not a lab image");

    public static string RequireName(string name)
    {
        Require(ObjectName().IsMatch(name), "the name is not a lab name");
        return name;
    }

    /// <summary>Lists may only be filtered on lab labels, so nothing else on the host is shown.</summary>
    public static string RequireLabelFilter(string filters)
    {
        var json = JsonNode.Parse(filters) as JsonObject;
        Require(json is not null && OnlyKeys(json, "label"), "lists must be filtered on a lab label");
        var labels = Strings(json!["label"]);
        Require(labels.Count == 1 && LabelFilter().IsMatch(labels[0]), "lists must be filtered on a lab label");
        return json.ToJsonString();
    }

    public static void RequireNetwork(JsonObject? body)
    {
        Require(body is not null && OnlyKeys(body, "Name", "Driver", "Internal", "IPAM", "Labels"),
            "the network has settings the lab does not use");
        RequireName(Text(body!["Name"]));
        Require(Drivers.Contains(Text(body["Driver"])), "the network driver is not allowed");
        Require(body["Internal"]?.GetValue<bool>() == true, "lab networks must be internal");
        RequireLabLabel(body["Labels"]);

        var ipam = body["IPAM"] as JsonObject;
        Require(ipam is not null && OnlyKeys(ipam, "Config"), "the network has address settings the lab does not use");
        var config = ipam!["Config"] as JsonArray;
        Require(config is { Count: 1 }, "a lab network has exactly one subnet");
        var subnet = config![0] as JsonObject;
        Require(subnet is not null && OnlyKeys(subnet, "Subnet") && LabSubnet().IsMatch(Text(subnet["Subnet"])),
            "the subnet is outside the lab address pool");
    }

    public static void RequireContainer(JsonObject? body)
    {
        Require(body is not null && OnlyKeys(body, "Image", "Hostname", "Labels", "Env", "HostConfig", "NetworkingConfig"),
            "the container has settings the lab does not use");
        RequireImage(Text(body!["Image"]));
        RequireLabLabel(body["Labels"]);
        Require(Strings(body["Env"]).All(e => Variable().IsMatch(e)), "the environment has a variable the lab does not use");

        var host = body["HostConfig"] as JsonObject;
        Require(host is not null && OnlyKeys(host,
                "CapDrop", "CapAdd", "SecurityOpt", "Privileged", "Memory", "NanoCpus", "PidsLimit", "Sysctls", "RestartPolicy"),
            "the container has host settings the lab does not use");
        Require(host!["Privileged"]?.GetValue<bool>() != true, "privileged containers are not allowed");
        Require(Strings(host["CapAdd"]).All(AddableCapabilities.Contains), "a capability outside NET_ADMIN and NET_RAW was asked for");
        Require(Strings(host["SecurityOpt"]) is ["no-new-privileges:true"], "no-new-privileges must be set");
        Require(Number(host["Memory"]) is > 0 and <= 1L << 30, "a memory limit of at most 1 GiB is required");
        Require(Number(host["NanoCpus"]) is > 0 and <= 2_000_000_000, "a CPU limit of at most 2 is required");
        Require(Number(host["PidsLimit"]) is > 0 and <= 512, "a process limit of at most 512 is required");
        Require(host["Sysctls"] is null or JsonObject, "sysctls are malformed");
        Require(host["Sysctls"] is not JsonObject sysctls || sysctls.All(s => Sysctls.Contains(s.Key)),
            "a sysctl the lab does not use was asked for");
        Require(host["RestartPolicy"] is null || Text(host["RestartPolicy"]?["Name"]) == "no", "restart policies are not allowed");

        var networking = body["NetworkingConfig"] as JsonObject;
        Require(networking is not null && OnlyKeys(networking, "EndpointsConfig"), "the container must be attached to lab networks only");
        var endpoints = networking!["EndpointsConfig"] as JsonObject;
        Require(endpoints is { Count: > 0 }, "the container must be attached to a lab network");
        foreach (var (network, settings) in endpoints!)
        {
            RequireName(network);
            var endpoint = settings as JsonObject;
            Require(endpoint is not null && OnlyKeys(endpoint, "IPAMConfig"), "the network attachment has settings the lab does not use");
            var ipam = endpoint!["IPAMConfig"] as JsonObject;
            Require(ipam is not null && OnlyKeys(ipam, "IPv4Address") && LabAddress().IsMatch(Text(ipam["IPv4Address"])),
                "the address is outside the lab address pool");
        }
    }

    /// <summary>
    /// A privileged exec is full control of the container and a way out of it,
    /// so only one exact command may use it: adding a route inside the pool.
    /// </summary>
    public static void RequireExec(JsonObject? body)
    {
        Require(body is not null && OnlyKeys(body, "Cmd", "AttachStdout", "AttachStderr", "Privileged", "User"),
            "the exec has settings the lab does not use");
        var command = Strings(body!["Cmd"]);
        Require(command.Count > 0, "the exec has no command");
        if (body["Privileged"]?.GetValue<bool>() != true) return;

        Require(command is ["ip", "route", "replace", _, "via", _]
                && LabSubnet().IsMatch(command[3]) && LabAddress().IsMatch(command[5]),
            "a privileged exec may only add a route inside the lab address pool");
    }

    public static void RequireExecStart(JsonObject? body) =>
        Require(body is not null && OnlyKeys(body, "Detach", "Tty")
                && body["Detach"]?.GetValue<bool>() == false && body["Tty"]?.GetValue<bool>() == false,
            "the exec may only be started attached and without a terminal");

    private static void RequireLabLabel(JsonNode? labels) =>
        Require(labels is JsonObject o && Text(o[LabLabel]) == "1", "the lab label is missing");

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Denied(reason);
    }

    private static bool OnlyKeys(JsonObject json, params string[] allowed) => json.All(p => allowed.Contains(p.Key));

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static long Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private static List<string> Strings(JsonNode? node)
    {
        if (node is null) return [];
        Require(node is JsonArray, "a list was expected");
        var items = ((JsonArray)node).Select(Text).ToList();
        Require(items.All(i => i.Length > 0), "a list of non-empty strings was expected");
        return items;
    }

    [GeneratedRegex(@"^azl-[0-9a-f]{8}-[a-z]+$")]
    private static partial Regex ObjectName();

    [GeneratedRegex(@"^azeroth\.[a-z]+(=[A-Za-z0-9-]+)?$")]
    private static partial Regex LabelFilter();

    [GeneratedRegex(@"^10\.77\.(25[0-5]|2[0-4]\d|1?\d?\d)\.0/24$")]
    private static partial Regex LabSubnet();

    [GeneratedRegex(@"^10\.77\.(25[0-5]|2[0-4]\d|1?\d?\d)\.(25[0-5]|2[0-4]\d|1?\d?\d)$")]
    private static partial Regex LabAddress();

    [GeneratedRegex(@"^(DEBUG_PORT|DB_PORT)=\d{1,5}$")]
    private static partial Regex Variable();
}
