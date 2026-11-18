using System.Text.Json;

namespace Game.Core;

/// <summary>
/// One judged report or wave in a played game. Failed holds the ids of the
/// checks that fell ("report" for a wrong report), so that any theme can name them.
/// </summary>
public sealed record AttemptRecord(int Wave, int Attempt, bool Held, string[] Failed, DateTimeOffset At);

/// <summary>One played game, from Start to the end or to Stop.</summary>
public sealed record GameRecord(
    string Id,
    string Scenario,
    string Theme,
    int Seed,
    int FirstWave,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    bool Completed,
    List<WaveOutcome> Waves,
    List<AttemptRecord> Attempts,
    Dictionary<int, int> Hints);

/// <summary>How one wave's skill stands over every game played.</summary>
public sealed record SkillProgress(
    int Wave,
    string Title,
    string Skill,
    int Played,
    int Held,
    int Mastered,
    int BestXp,
    int FailedAttempts,
    int HintsUsed,
    string? StuckOn);

/// <summary>
/// The player's games, kept in a JSON file in their own directory
/// (~/.azeroth/history.json, or AZEROTH_HOME). One player, a few hundred games
/// at most, so the whole file is read once and rewritten on every change.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<GameRecord> _games;

    public HistoryStore(string path)
    {
        Path = path;
        _games = File.Exists(path) ? Read(path) : [];
    }

    public string Path { get; }

    public static HistoryStore Default()
    {
        var home = Environment.GetEnvironmentVariable("AZEROTH_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azeroth");
        return new HistoryStore(System.IO.Path.Combine(home, "history.json"));
    }

    public IReadOnlyList<GameRecord> Games
    {
        get
        {
            lock (_lock) return [.. _games];
        }
    }

    public void Save(GameRecord game)
    {
        lock (_lock)
        {
            var at = _games.FindIndex(g => g.Id == game.Id);
            if (at >= 0) _games[at] = game;
            else _games.Add(game);

            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_games, Json));
            File.Move(temporary, Path, overwrite: true);
        }
    }

    /// <summary>Progress per wave of a scenario, over every game of it.</summary>
    public IReadOnlyList<SkillProgress> Progress(Scenario scenario)
    {
        var games = Games.Where(g => g.Scenario == scenario.Id).ToList();
        return scenario.Waves.OrderBy(w => w.Number).Select(wave =>
        {
            var outcomes = games.SelectMany(g => g.Waves).Where(o => o.Wave == wave.Number).ToList();
            var attempts = games.SelectMany(g => g.Attempts).Where(a => a.Wave == wave.Number).ToList();
            var failed = attempts.Where(a => !a.Held).ToList();
            var stuckOn = failed.SelectMany(a => a.Failed)
                .GroupBy(f => f)
                .OrderByDescending(g => g.Count())
                .Select(g => CheckTitle(scenario, g.Key))
                .FirstOrDefault();
            return new SkillProgress(
                wave.Number,
                wave.Title,
                wave.Skill,
                Played: attempts.Count > 0 ? games.Count(g => g.Attempts.Any(a => a.Wave == wave.Number)) : 0,
                Held: outcomes.Count,
                Mastered: outcomes.Count(o => o.Mastered),
                BestXp: outcomes.Count > 0 ? outcomes.Max(o => o.Xp) : 0,
                FailedAttempts: failed.Count,
                HintsUsed: games.Sum(g => g.Hints.GetValueOrDefault(wave.Number)),
                StuckOn: stuckOn);
        }).ToList();
    }

    private static string CheckTitle(Scenario scenario, string id) =>
        id == "report"
            ? "The report"
            : scenario.Waves.SelectMany(w => w.Checks ?? []).FirstOrDefault(c => c.Id == id)?.Title ?? id;

    /// <summary>The first wave not yet mastered: where practice pays most.</summary>
    public int SuggestedWave(Scenario scenario) =>
        Progress(scenario).FirstOrDefault(p => p.Mastered == 0)?.Wave ?? scenario.Waves.Min(w => w.Number);

    private static List<GameRecord> Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GameRecord>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            // A damaged history must not stop the game. Keep the damaged file aside.
            File.Copy(path, path + ".damaged", overwrite: true);
            return [];
        }
    }
}

/// <summary>Writes a game to the history as it is played.</summary>
public sealed class GameRecorder(HistoryStore store, Scenario scenario, Theme theme, int seed, int firstWave)
{
    private GameRecord _game = new(
        Guid.NewGuid().ToString("N")[..12], scenario.Id, theme.Id, seed, firstWave,
        DateTimeOffset.Now, null, false, [], [], []);

    public void On(GameEvent e)
    {
        switch (e)
        {
            case HintGiven hint:
                var hints = new Dictionary<int, int>(_game.Hints);
                hints[hint.Wave] = hints.GetValueOrDefault(hint.Wave) + 1;
                _game = _game with { Hints = hints };
                break;
            case AttemptJudged judged:
                var failed = judged.Held
                    ? []
                    : judged.Outcomes.Count > 0
                        ? judged.Outcomes.Where(o => !o.Result.Passed).Select(o => o.Spec.Id).ToArray()
                        : ["report"];
                _game = _game with { Attempts = [.. _game.Attempts, new AttemptRecord(judged.Wave, judged.Attempt, judged.Held, failed, DateTimeOffset.Now)] };
                break;
            case WaveHeld held:
                _game = _game with { Waves = [.. _game.Waves, held.Outcome] };
                break;
            case GameCompleted:
                _game = _game with { Completed = true, Ended = DateTimeOffset.Now };
                break;
            default:
                return;
        }
        store.Save(_game);
    }

    /// <summary>Marks the game as ended, if anything was played in it.</summary>
    public void End()
    {
        if (_game.Ended is not null || _game.Attempts.Count == 0) return;
        _game = _game with { Ended = DateTimeOffset.Now };
        store.Save(_game);
    }
}
