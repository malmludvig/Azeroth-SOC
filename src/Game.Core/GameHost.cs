using Game.Checks;
using Game.Orchestration;

namespace Game.Core;

public enum GamePhase
{
    Idle,
    Building,
    Playing,
    Judging,
    Completed,
    Failed,
}

public sealed record LogEntry(DateTimeOffset At, string Kind, string Title, string Text);

/// <summary>
/// A port on the notice board as seen from outside, measured live: can the
/// raiders on the plains reach it, and can the garrison? Null means not yet
/// measured.
/// </summary>
public sealed record Door(string Name, int Port, bool Public, bool? OutsidersReach, bool? GarrisonReach);

/// <summary>Everything a page needs to draw the game. Replaced whole on every change.</summary>
public sealed record GameView(
    GamePhase Phase,
    string? RunId,
    string ScenarioTitle,
    IReadOnlyList<(int Number, string Title)> Waves,
    int? CurrentWave,
    string? Briefing,
    IReadOnlyList<LogEntry> Log,
    IReadOnlyList<WaveOutcome> Outcomes,
    double? Health,
    int? Intrusions,
    IReadOnlyList<Door> Doors,
    string? GateState,
    string? Error)
{
    public static readonly GameView Empty = new(
        GamePhase.Idle, null, "", [], null, null, [], [], null, null, [], null, null);

    public int TotalXp => Outcomes.Sum(o => o.Xp);

    public bool Active => Phase is GamePhase.Building or GamePhase.Playing or GamePhase.Judging;
}

/// <summary>
/// The game master inside the web backend: one game at a time, started and
/// stopped from the page. It runs a <see cref="GameSession"/>, turns its events
/// into a <see cref="GameView"/>, and between waves keeps a live picture of
/// which doors on the notice board the gate leaves open.
/// </summary>
public sealed class GameHost(DockerEngine docker, LabOrchestrator lab, HistoryStore history, Theme theme) : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);
    private static readonly string[] PortNames = ["http", "management", "debug", "database"];

    private readonly object _lock = new();
    private readonly SemaphoreSlim _starting = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _game;
    private Task? _probe;
    private GameSession? _session;
    private Scenario? _scenario;
    private LabRun? _run;
    private GameRecorder? _recorder;

    public Theme Theme => theme;

    public HistoryStore History => history;

    /// <summary>The scenario as the player sees it, in this host's theme.</summary>
    public Scenario LoadScenario() => theme.Apply(Scenario.Load(Scenario.Locate(SentinelHill.Scenario)));

    public GameView View { get; private set; } = GameView.Empty;

    /// <summary>Raised after every change of <see cref="View"/>, on whatever thread made it.</summary>
    public event Action? Changed;

    public async Task StartAsync(int firstWave = 0, int? seed = null)
    {
        if (!await _starting.WaitAsync(0)) return;
        try
        {
            if (View.Active) return;
            await StopAsync();

            var scenario = LoadScenario();
            scenario.Wave(firstWave);
            Update(_ => GameView.Empty with
            {
                Phase = GamePhase.Building,
                ScenarioTitle = scenario.Title,
                Waves = scenario.Waves.OrderBy(w => w.Number).Select(w => (w.Number, w.Title)).ToList(),
            });

            LabRun run;
            try
            {
                var existing = await lab.ListAsync();
                if (existing.Count == 0) run = await lab.UpAsync(SentinelHill.Create(seed ?? Random.Shared.Next()));
                else if (existing[0].IsRunning) run = existing[0];
                else run = await lab.ResetAsync(existing[0]);
            }
            catch (Exception e) when (e is LabException or DockerException or HttpRequestException)
            {
                Fail(e.Message);
                return;
            }

            var stop = new CancellationTokenSource();
            var session = new GameSession(docker, run, scenario, events: OnEvent);
            var recorder = new GameRecorder(history, scenario, theme, run.Spec.Seed, firstWave);
            lock (_lock)
            {
                _recorder = recorder;
                _stop = stop;
                _session = session;
                _scenario = scenario;
                _run = run;
            }
            Update(v => v with { Phase = GamePhase.Playing, RunId = run.RunId });

            _game = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync(firstWave, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stopped from the page.
                }
                catch (Exception e)
                {
                    Fail(e.Message);
                }
            });
            _probe = Task.Run(() => ProbeLoopAsync(run, stop.Token));
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>Ends the game. The lab and the player's rules in the gate stay.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? stop;
        GameSession? session;
        GameRecorder? recorder;
        lock (_lock)
        {
            stop = _stop;
            session = _session;
            recorder = _recorder;
            _stop = null;
            _session = null;
            _recorder = null;
        }
        if (stop is null) return;

        await stop.CancelAsync();
        foreach (var task in new[] { _game, _probe })
        {
            if (task is null) continue;
            try
            {
                await task;
            }
            catch (Exception e) when (e is OperationCanceledException or DockerException or LabException or HttpRequestException)
            {
                // Already ended, or the lab is gone.
            }
        }
        try
        {
            if (session is not null) await session.CloseAsync();
        }
        catch (Exception e) when (e is DockerException or LabException or HttpRequestException)
        {
            // The lab is gone; there is nothing to close.
        }
        stop.Dispose();
        recorder?.End();

        Update(v => v.Phase is GamePhase.Completed or GamePhase.Failed ? v : v with { Phase = GamePhase.Idle });
    }

    public void Dispose() => _stop?.Cancel();

    private void OnEvent(GameEvent e)
    {
        try
        {
            _recorder?.On(e);
        }
        catch (IOException)
        {
            // A history that cannot be written must not stop the game.
        }

        var now = DateTimeOffset.Now;
        switch (e)
        {
            case WaveStarted started:
                Update(v => v with
                {
                    CurrentWave = started.Wave.Number,
                    Briefing = started.Briefing,
                    Health = null,
                    Intrusions = null,
                    Log = Append(v.Log, new LogEntry(now, "wave", $"Wave {started.Wave.Number}: {started.Wave.Title}", "")),
                });
                break;

            case HintGiven hint:
                Update(v => v with { Log = Append(v.Log, new LogEntry(now, "hint", $"Hint {hint.Level}", hint.Text)) });
                break;

            case JudgingStarted judging:
                Update(v => v with
                {
                    Phase = GamePhase.Judging,
                    Log = Append(v.Log, new LogEntry(now, "bell", $"The bell rings (attempt {judging.Attempt})", "")),
                });
                break;

            case AttemptJudged judged:
                var (health, intrusions) = Meters(judged.Outcomes);
                Update(v => v with
                {
                    Phase = GamePhase.Playing,
                    Health = judged.Outcomes.Count > 0 ? health : v.Health,
                    Intrusions = judged.Outcomes.Count > 0 ? intrusions : v.Intrusions,
                    Log = Append(v.Log, new LogEntry(now, judged.Held ? "held" : "fell",
                        judged.Held ? "Held" : "Not held yet", judged.Text.TrimEnd())),
                });
                break;

            case WaveHeld held:
                Update(v => v with
                {
                    Outcomes = [.. v.Outcomes, held.Outcome],
                    Log = Append(v.Log, new LogEntry(now, "xp",
                        $"Wave {held.Outcome.Wave} held: +{held.Outcome.Xp} XP" + (held.Outcome.Mastered ? ", mastered" : ""),
                        "To think about: " + held.Question)),
                });
                break;

            case GameCompleted completed:
                Update(v => v with
                {
                    Phase = GamePhase.Completed,
                    Briefing = completed.Ending,
                    Log = Append(v.Log, new LogEntry(now, "done", "Scenario complete", "")),
                });
                break;
        }
    }

    // Health is the worst share of citizens served; intrusions count every
    // forbidden door reached and every source that flooded past the limit.
    private (double Health, int Intrusions) Meters(IReadOnlyList<CheckOutcome> outcomes)
    {
        var health = 1.0;
        var intrusions = 0;
        foreach (var outcome in outcomes)
        {
            var measures = outcome.Result.Measures;
            if (measures is null) continue;
            if (measures.TryGetValue("citizen-rate", out var rate)) health = Math.Min(health, rate);
            if (measures.TryGetValue("forbidden-reached", out var reached)) intrusions += (int)reached;
            if (measures.TryGetValue("worst-rate", out var worst) && worst > outcome.Spec.MaxPerSecond) intrusions++;
        }
        return (health, intrusions);
    }

    // Between waves, look at the board from the plains every few seconds, so
    // the page shows what the player's rules do while they write them. Not
    // during a wave: the extra connections would disturb the measurements.
    // Not in wave 0: which ports answer is what the player has to find out.
    private async Task ProbeLoopAsync(LabRun run, CancellationToken ct)
    {
        var context = new CheckContext(docker, run);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var view = View;
                if (view.Phase == GamePhase.Playing && view.CurrentWave is > 0)
                {
                    var outsiders = await Task.WhenAll(PortNames.Select(n => context.CanConnectAsync("defias", context.Port(n), ct)));
                    var garrison = await context.CanConnectAsync("garrison", context.Port("management"), ct);
                    var state = (await GateState.ReadAsync(docker, run, ct)).Describe();
                    var doors = PortNames
                        .Select((name, i) => new Door(name, context.Port(name), name == "http", outsiders[i],
                            name == "management" ? garrison : null))
                        .ToList();
                    if (View.Phase == GamePhase.Playing) Update(v => v with { Doors = doors, GateState = state });
                }
                else if (view.Phase == GamePhase.Playing && view.GateState is null)
                {
                    var state = (await GateState.ReadAsync(docker, run, ct)).Describe();
                    Update(v => v with { GateState = state });
                }
            }
            catch (Exception e) when (e is DockerException or LabException or HttpRequestException)
            {
                // A probe that fails now is retried next round; the game itself reports a lost lab.
            }

            try
            {
                await Task.Delay(ProbeInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Fail(string message) => Update(v => v with { Phase = GamePhase.Failed, Error = message });

    private void Update(Func<GameView, GameView> change)
    {
        lock (_lock) View = change(View);
        Changed?.Invoke();
    }

    private static IReadOnlyList<LogEntry> Append(IReadOnlyList<LogEntry> log, LogEntry entry) => [.. log.TakeLast(99), entry];
}
