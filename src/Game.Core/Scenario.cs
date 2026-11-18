using System.Text.Json;
using Game.Checks;

namespace Game.Core;

public sealed record HintSpec(int Level, string[] Text, string When = "always");

public sealed record CheckSpec(
    string Id,
    string Type,
    string Title,
    string? From = null,
    string[]? Open = null,
    string[]? Closed = null,
    int Requests = 20,
    double Min = 0.99,
    string[]? Attackers = null,
    string? Citizens = null,
    double MaxPerSecond = 20);

public sealed record WaveSpec(
    int Number,
    string Title,
    string Kind,
    int Xp,
    string[] Briefing,
    string Question,
    HintSpec[] Hints,
    CheckSpec[]? Checks = null,
    string Skill = "",
    string[]? Answer = null,
    string[]? QuestionAnswer = null)
{
    public const string Report = "report";
    public const string Traffic = "traffic";
}

public sealed record CheckOutcome(CheckSpec Spec, CheckResult Result);

public sealed record WaveVerdict(int Wave, IReadOnlyList<CheckOutcome> Outcomes)
{
    public bool Passed => Outcomes.All(o => o.Result.Passed);
}

/// <summary>
/// A scenario as written in lab/scenarios: its waves, what each wave checks,
/// and the text the player sees. The topology and the checks' code live
/// elsewhere; this file decides which checks a wave uses and with what numbers.
/// </summary>
public sealed record Scenario(string Id, string Title, string[] Intro, string[] Outro, WaveSpec[] Waves)
{
    public static readonly string[] HintConditions =
        ["always", "no-forward-chain", "policy-accept", "no-established", "no-rate-limit"];

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Scenario Load(string path)
    {
        Scenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json);
        }
        catch (JsonException e)
        {
            throw new ScenarioException($"{path} is not a valid scenario: {e.Message}");
        }
        if (scenario is null) throw new ScenarioException($"{path} is empty.");
        scenario.Validate(path);
        return scenario;
    }

    /// <summary>Finds lab/scenarios/&lt;id&gt;.json from the working directory or the program's own location.</summary>
    public static string Locate(string id) => LocateInRepository(Path.Combine("lab", "scenarios", id + ".json"));

    internal static string LocateInRepository(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new ScenarioException($"Cannot find {relative}. Run from inside the repository.");
    }

    public WaveSpec Wave(int number) =>
        Waves.FirstOrDefault(w => w.Number == number)
        ?? throw new ScenarioException($"Scenario {Id} has no wave {number}.");

    public IEnumerable<int> TrafficWaves => Waves.Where(w => w.Kind == WaveSpec.Traffic).Select(w => w.Number);

    /// <summary>
    /// The checks of a wave together with those of every traffic wave before it:
    /// a defence must not open up what it closed.
    /// </summary>
    public IReadOnlyList<(CheckSpec Spec, ICheck Check)> ChecksFor(int wave)
    {
        if (Wave(wave).Kind != WaveSpec.Traffic)
            throw new ScenarioException($"Wave {wave} is judged by a report, not by traffic.");
        return Waves
            .Where(w => w.Kind == WaveSpec.Traffic && w.Number <= wave)
            .SelectMany(w => w.Checks ?? [])
            .Select(spec => (spec, Build(spec)))
            .ToList();
    }

    public async Task<WaveVerdict> JudgeAsync(int wave, CheckContext context, CancellationToken ct = default)
    {
        var outcomes = new List<CheckOutcome>();
        foreach (var (spec, check) in ChecksFor(wave))
            outcomes.Add(new CheckOutcome(spec, await check.RunAsync(context, ct)));
        return new WaveVerdict(wave, outcomes);
    }

    private static ICheck Build(CheckSpec spec) => spec.Type switch
    {
        "ports" => new PortsCheck(spec.Id, Required(spec, spec.From, "from"), spec.Open ?? [], spec.Closed ?? []),
        "success-rate" => new SuccessRateCheck(spec.Id, Required(spec, spec.From, "from"), spec.Requests, spec.Min),
        "flood" => new FloodCheck(
            spec.Id, Required(spec, spec.Attackers, "attackers"), Required(spec, spec.Citizens, "citizens"),
            spec.MaxPerSecond, spec.Min),
        _ => throw new ScenarioException($"Check {spec.Id} has unknown type '{spec.Type}'."),
    };

    private static T Required<T>(CheckSpec spec, T? value, string name) where T : class =>
        value ?? throw new ScenarioException($"Check {spec.Id} of type {spec.Type} needs '{name}'.");

    private void Validate(string path)
    {
        void Fail(string message) => throw new ScenarioException($"{path}: {message}");

        if (Waves is null || Waves.Length == 0) throw new ScenarioException($"{path}: the scenario has no waves.");
        if (Waves.Select(w => w.Number).Distinct().Count() != Waves.Length) Fail("two waves share a number.");

        var checkIds = new HashSet<string>();
        foreach (var wave in Waves)
        {
            if (wave.Kind is not (WaveSpec.Report or WaveSpec.Traffic)) Fail($"wave {wave.Number} has unknown kind '{wave.Kind}'.");
            if (wave.Kind == WaveSpec.Traffic && (wave.Checks is null || wave.Checks.Length == 0))
                Fail($"wave {wave.Number} is a traffic wave without checks.");

            foreach (var check in wave.Checks ?? [])
            {
                if (!checkIds.Add(check.Id)) Fail($"check id '{check.Id}' is used twice.");
                Build(check);
            }

            // Every level needs a hint that applies whatever the gate looks like.
            foreach (var level in new[] { 1, 2, 3 })
            {
                if (!(wave.Hints ?? []).Any(h => h.Level == level && h.When == "always"))
                    Fail($"wave {wave.Number} has no unconditional hint at level {level}.");
            }
            foreach (var hint in wave.Hints ?? [])
            {
                if (!HintConditions.Contains(hint.When)) Fail($"wave {wave.Number} has a hint with unknown condition '{hint.When}'.");
            }
        }
    }
}

public sealed class ScenarioException(string message) : Exception(message);
