using Game.Orchestration;

namespace Game.Core;

/// <summary>The answer to one wave, for this run's addresses and ports.</summary>
public sealed record Answer(int Wave, string Title, string Steps, string Discussion);

/// <summary>
/// The answers of cheat mode. They come from the scenario file, where every
/// wave has the commands that settle it and an answer to its question, and are
/// filled in with the run's own networks, addresses and ports. The self-test
/// runs every answer, so an answer that would not hold is caught there.
/// </summary>
public static class Answers
{
    public static IReadOnlyList<Answer> For(Scenario scenario, LabSpec spec) =>
        scenario.Waves.OrderBy(w => w.Number)
            .Select(w => new Answer(
                w.Number,
                w.Title,
                Render(string.Join('\n', w.Answer ?? []), spec),
                Render(string.Join('\n', w.QuestionAnswer ?? []), spec)))
            .ToList();

    public static string Render(string text, LabSpec spec)
    {
        string Port(string name) => spec.Parameters["web." + name];
        var ports = new[] { "http", "management", "debug", "database" }.Select(n => int.Parse(Port(n))).Order();
        return text
            .Replace("{web}", spec.Node("web").AddressOn(SentinelHill.Dmz))
            .Replace("{garrison}", spec.Node("garrison").AddressOn(SentinelHill.Plains))
            .Replace("{plains}", spec.Networks.First(n => n.Key == SentinelHill.Plains).Subnet)
            .Replace("{dmz}", spec.Networks.First(n => n.Key == SentinelHill.Dmz).Subnet)
            .Replace("{ports}", string.Join(",", ports))
            .Replace("{http}", Port("http"))
            .Replace("{management}", Port("management"))
            .Replace("{debug}", Port("debug"))
            .Replace("{database}", Port("database"));
    }
}
