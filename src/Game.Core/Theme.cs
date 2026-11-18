using System.Text.Json;
using System.Text.RegularExpressions;

namespace Game.Core;

public sealed record WaveText(string? Title = null, string[]? Briefing = null, string? Question = null);

public sealed record ScenarioText(
    string? Title = null,
    string[]? Intro = null,
    string[]? Outro = null,
    Dictionary<string, WaveText>? Waves = null,
    Dictionary<string, string>? Checks = null);

/// <summary>
/// Names and story laid over a scenario. A theme replaces text only: wave
/// titles, briefings and questions, check titles, and the names the page shows
/// for places and people. What a wave checks, and how, stays in the scenario.
///
/// A theme lives in themes/theme-&lt;id&gt;.json. The neutral theme changes
/// nothing. The one in use is picked with AZEROTH_THEME (default: wow).
/// </summary>
public sealed partial record Theme(
    string Id,
    string Name,
    Dictionary<string, string>? Names = null,
    Dictionary<string, ScenarioText>? Scenarios = null)
{
    public const string Variable = "AZEROTH_THEME";
    public const string DefaultId = "wow";

    /// <summary>The names the page uses when a theme does not give its own.</summary>
    public static readonly IReadOnlyDictionary<string, string> NeutralNames = new Dictionary<string, string>
    {
        ["game"] = "Azeroth SOC",
        ["plains"] = "The plains",
        ["town"] = "The town",
        ["gate"] = "Gate",
        ["board"] = "Notice board",
        ["citizens"] = "Citizens",
        ["garrison"] = "Garrison",
        ["raiders"] = "Raiders",
        ["travellers"] = "Travellers",
        ["health"] = "Health of the town",
    };

    public static Theme Current(string? id = null)
    {
        id ??= Environment.GetEnvironmentVariable(Variable);
        return Load(string.IsNullOrWhiteSpace(id) ? DefaultId : id.Trim());
    }

    public static Theme Load(string id)
    {
        if (!ThemeId().IsMatch(id)) throw new ScenarioException($"'{id}' is not a theme name. Use letters, digits and dashes.");
        if (!Available().Contains(id)) throw new ScenarioException($"There is no theme '{id}' (no file themes/theme-{id}.json).");
        var path = Scenario.LocateInRepository(Path.Combine("themes", $"theme-{id}.json"));
        Theme? theme;
        try
        {
            theme = JsonSerializer.Deserialize<Theme>(File.ReadAllText(path), Scenario.Json);
        }
        catch (JsonException e)
        {
            throw new ScenarioException($"{path} is not a valid theme: {e.Message}");
        }
        if (theme is null || theme.Id != id) throw new ScenarioException($"{path} must have \"id\": \"{id}\".");
        foreach (var key in (IEnumerable<string>?)theme.Names?.Keys ?? [])
        {
            if (!NeutralNames.ContainsKey(key)) throw new ScenarioException($"{path}: unknown name '{key}'.");
        }
        return theme;
    }

    /// <summary>Every theme in the repository, by id.</summary>
    public static IReadOnlyList<string> Available()
    {
        var dir = Path.GetDirectoryName(Scenario.LocateInRepository(Path.Combine("themes", "theme-neutral.json")))!;
        return Directory.GetFiles(dir, "theme-*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)["theme-".Length..])
            .Order()
            .ToList();
    }

    public string Label(string key) =>
        Names?.GetValueOrDefault(key) ?? NeutralNames.GetValueOrDefault(key) ?? key;

    /// <summary>
    /// The scenario with this theme's text. Refuses text that would change
    /// what the player has to do: a themed briefing must keep every
    /// {placeholder} and every line with a quest command of the original.
    /// </summary>
    public Scenario Apply(Scenario scenario)
    {
        var text = Scenarios?.GetValueOrDefault(scenario.Id);
        if (text is null) return scenario;

        void Fail(string message) => throw new ScenarioException($"Theme {Id}, scenario {scenario.Id}: {message}");

        foreach (var key in (IEnumerable<string>?)text.Waves?.Keys ?? [])
        {
            if (!int.TryParse(key, out var number) || scenario.Waves.All(w => w.Number != number))
                Fail($"there is no wave '{key}'.");
        }
        var checkIds = scenario.Waves.SelectMany(w => w.Checks ?? []).Select(c => c.Id).ToHashSet();
        foreach (var id in (IEnumerable<string>?)text.Checks?.Keys ?? [])
        {
            if (!checkIds.Contains(id)) Fail($"there is no check '{id}'.");
        }

        var waves = scenario.Waves.Select(wave =>
        {
            var themed = text.Waves?.GetValueOrDefault(wave.Number.ToString());
            if (themed?.Briefing is { } briefing)
            {
                var missing = Placeholders(wave.Briefing).Except(Placeholders(briefing)).ToList();
                if (missing.Count > 0) Fail($"wave {wave.Number}'s briefing lost {string.Join(", ", missing)}.");
                foreach (var line in wave.Briefing.Where(l => l.Contains("quest ", StringComparison.Ordinal)))
                {
                    if (!briefing.Contains(line)) Fail($"wave {wave.Number}'s briefing lost the line: {line.Trim()}");
                }
            }

            return wave with
            {
                Title = themed?.Title ?? wave.Title,
                Briefing = themed?.Briefing ?? wave.Briefing,
                Question = themed?.Question ?? wave.Question,
                Checks = wave.Checks?.Select(c => c with { Title = text.Checks?.GetValueOrDefault(c.Id) ?? c.Title }).ToArray(),
            };
        }).ToArray();

        return scenario with
        {
            Title = text.Title ?? scenario.Title,
            Intro = text.Intro ?? scenario.Intro,
            Outro = text.Outro ?? scenario.Outro,
            Waves = waves,
        };
    }

    private static IEnumerable<string> Placeholders(IEnumerable<string> lines) =>
        lines.SelectMany(l => PlaceholderPattern().Matches(l).Select(m => m.Value)).Distinct();

    [GeneratedRegex(@"\{[a-z]+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^[a-z0-9-]{1,32}$")]
    private static partial Regex ThemeId();
}
