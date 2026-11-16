using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Orchestration;

namespace Game.Checks;

/// <summary>
/// What the gate's ruleset looks like right now, read with "nft -j list ruleset".
/// This is for feedback and for choosing a hint. It never decides a verdict:
/// the player is root in the gate, and there are many right ways to write a rule.
/// </summary>
public sealed record GateState(
    bool HasForwardChain,
    string? ForwardPolicy,
    int ForwardRules,
    bool AcceptsEstablished,
    bool UsesRateLimit)
{
    public static async Task<GateState> ReadAsync(DockerEngine docker, LabRun run, CancellationToken ct = default)
    {
        var result = await docker.ExecAsync(run.ContainerName("gate"), ["nft", "-j", "list", "ruleset"], ct: ct);
        if (!result.Succeeded) throw new LabException($"Could not read the gate's ruleset: {result.Output.Trim()}");
        return Parse(result.Output);
    }

    public static GateState Parse(string json)
    {
        JsonArray? items;
        try
        {
            items = JsonNode.Parse(json)?["nftables"] as JsonArray;
        }
        catch (JsonException)
        {
            items = null;
        }
        if (items is null) return new GateState(false, null, 0, false, false);

        var forwardChains = items
            .Select(i => i?["chain"])
            .Where(c => c?["hook"]?.GetValue<string>() == "forward")
            .ToList();
        var chainKeys = forwardChains
            .Select(c => (c!["family"]?.GetValue<string>(), c["table"]?.GetValue<string>(), c["name"]?.GetValue<string>()))
            .ToHashSet();

        var rules = items
            .Select(i => i?["rule"])
            .Where(r => r is not null && chainKeys.Contains(
                (r["family"]?.GetValue<string>(), r["table"]?.GetValue<string>(), r["chain"]?.GetValue<string>())))
            .ToList();

        return new GateState(
            HasForwardChain: forwardChains.Count > 0,
            ForwardPolicy: forwardChains.FirstOrDefault()?["policy"]?.GetValue<string>(),
            ForwardRules: rules.Count,
            AcceptsEstablished: rules.Any(r => MentionsEstablished(r!["expr"])),
            UsesRateLimit: rules.Any(r => HasKey(r!["expr"], "limit")));
    }

    public string Describe()
    {
        if (!HasForwardChain) return "no chain on the forward hook: everything passes the gate";
        var parts = new List<string>
        {
            $"forward policy {ForwardPolicy ?? "accept"}",
            $"{ForwardRules} rule(s)",
            AcceptsEstablished ? "established flows accepted" : "no rule for established flows",
        };
        if (UsesRateLimit) parts.Add("rate limiting in use");
        return string.Join(", ", parts);
    }

    private static bool MentionsEstablished(JsonNode? expressions) =>
        Walk(expressions).Any(node =>
            node is JsonObject match
            && match["left"]?["ct"]?["key"]?.GetValue<string>() == "state"
            && Walk(match["right"]).Any(v =>
                v is JsonValue value && value.TryGetValue<string>(out var text) && text == "established"));

    private static bool HasKey(JsonNode? node, string key) =>
        Walk(node).Any(n => n is JsonObject o && o.ContainsKey(key));

    private static IEnumerable<JsonNode> Walk(JsonNode? node)
    {
        if (node is null) yield break;
        yield return node;
        var children = node switch
        {
            JsonObject o => o.Select(p => p.Value),
            JsonArray a => a.AsEnumerable(),
            _ => [],
        };
        foreach (var child in children)
            foreach (var inner in Walk(child))
                yield return inner;
    }
}
