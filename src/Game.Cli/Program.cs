using System.Diagnostics;
using Game.Checks;
using Game.Core;
using Game.Orchestration;

// Runs a lab without the game client.
//
//   up [--seed N] [--replace]   build a new run
//   status                      show the current run
//   reset                       rebuild the current run from the images, same seed
//   down                        remove the current run
//   sweep [--expired]           remove every lab run, or only those past their lifetime
//   soak [N]                    up, reset, down N times and check that nothing is left behind
//   check <wave>                judge the current run against a wave (1 to 3)
//   selftest                    judge known right and wrong defences and require the expected verdicts
//   play [--wave N] [--seed N]  play the scenario: you work in the gate, this terminal is the game master
//   playtest                    play the whole scenario automatically and require the expected outcome
//   history                     show your progress per wave over every game played
//   themetest                   load every theme and check that none changes what the player must do
//   hosttest                    play through the game host behind the web page and check what it shows
//   termtest                    use the web terminal the way the browser does (needs src/Game.Api running)
//   isolation                   try to get out of the lab from every container; every attempt must fail
//   proxytest                   send forbidden requests to the Docker proxy; every one must be refused

var command = args.Length > 0 ? args[0] : "help";
var options = args.Skip(1).ToArray();

try
{
    using var docker = new DockerEngine();
    var lab = new LabOrchestrator(docker, Console.WriteLine);

    switch (command)
    {
        case "up": return await Up(lab, options);
        case "status": return await Status(lab);
        case "reset": return await Reset(lab);
        case "down": return await Down(lab);
        case "sweep": return await Sweep(lab, options);
        case "soak": return await Soak(lab, options);
        case "check": return await Check(docker, lab, options);
        case "selftest": return await SelfTest.RunAsync(docker, lab);
        case "play": return await Play(docker, lab, options);
        case "playtest": return await PlayTest.RunAsync(docker, lab);
        case "history": return History();
        case "themetest": return ThemeTest.Run();
        case "hosttest": return await HostTest.RunAsync(docker, lab);
        case "termtest": return await TermTest.RunAsync(lab);
        case "isolation": return await IsolationTest.RunAsync(docker, lab);
        case "proxytest": return await ProxyTest.RunAsync();
        default:
            Console.WriteLine("Usage: Game.Cli up [--seed N] [--replace] | status | reset | down | sweep [--expired] | soak [N] | check <wave> | selftest | isolation | proxytest | play [--wave N] | playtest | hosttest | termtest | history | themetest");
            return command == "help" ? 0 : 2;
    }
}
catch (ScenarioException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (LabException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (DockerException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (HttpRequestException e)
{
    var host = Environment.GetEnvironmentVariable("DOCKER_HOST");
    Console.Error.WriteLine($"Cannot reach Docker at {(string.IsNullOrWhiteSpace(host) ? "unix:///var/run/docker.sock" : host)}: {e.Message}");
    Console.Error.WriteLine(string.IsNullOrWhiteSpace(host)
        ? "Is Docker Desktop running? Check with: docker version"
        : "DOCKER_HOST is set. If it points at the lab's Docker proxy, start the proxy, or clear it with: unset DOCKER_HOST");
    return 1;
}

static async Task<int> Up(LabOrchestrator lab, string[] options)
{
    var existing = await lab.ListAsync();
    if (existing.Count > 0)
    {
        if (!options.Contains("--replace"))
        {
            Console.Error.WriteLine($"Run {existing[0].RunId} already exists. Use: up --replace, reset or down.");
            return 1;
        }
        await lab.SweepAsync(expiredOnly: false);
    }

    var seed = Random.Shared.Next();
    var seedAt = Array.IndexOf(options, "--seed");
    if (seedAt >= 0 && (seedAt + 1 >= options.Length || !int.TryParse(options[seedAt + 1], out seed)))
    {
        Console.Error.WriteLine("--seed needs a whole number.");
        return 2;
    }

    Print(await lab.UpAsync(SentinelHill.Create(seed)));
    return 0;
}

static async Task<int> Status(LabOrchestrator lab)
{
    var runs = await lab.ListAsync();
    if (runs.Count == 0)
    {
        Console.WriteLine("No lab is running.");
        return 0;
    }
    foreach (var run in runs) Print(run);
    return 0;
}

static async Task<int> Reset(LabOrchestrator lab)
{
    var runs = await lab.ListAsync();
    if (runs.Count == 0)
    {
        Console.Error.WriteLine("No lab is running. Use: up");
        return 1;
    }
    Print(await lab.ResetAsync(runs[0]));
    return 0;
}

static async Task<int> Down(LabOrchestrator lab)
{
    var removed = await lab.SweepAsync(expiredOnly: false);
    Console.WriteLine(removed == 0 ? "No lab was running." : "Lab removed.");
    return 0;
}

static async Task<int> Sweep(LabOrchestrator lab, string[] options)
{
    var removed = await lab.SweepAsync(expiredOnly: options.Contains("--expired"));
    Console.WriteLine($"Removed {removed} run(s).");
    return 0;
}

static async Task<int> Soak(LabOrchestrator lab, string[] options)
{
    var rounds = options.Length > 0 && int.TryParse(options[0], out var n) && n > 0 ? n : 10;
    await lab.SweepAsync(expiredOnly: false);

    for (var round = 1; round <= rounds; round++)
    {
        var clock = Stopwatch.StartNew();
        var run = await lab.UpAsync(SentinelHill.Create(Random.Shared.Next()));
        var up = clock.Elapsed;

        var again = await lab.ResetAsync(run);
        if (again.Spec.Seed != run.Spec.Seed || again.Nodes.Count != run.Nodes.Count
            || again.Nodes.Any(node => node.State != "running"))
        {
            Console.Error.WriteLine($"Round {round}: the reset run does not match the original.");
            return 1;
        }

        await lab.DownAsync(again.RunId);
        var left = await lab.CountResourcesAsync();
        if (!left.IsEmpty)
        {
            Console.Error.WriteLine(
                $"Round {round}: {left.Containers} container(s) and {left.Networks} network(s) left behind.");
            return 1;
        }

        Console.WriteLine(
            $"Round {round,2}/{rounds}  seed {run.Spec.Seed,-10}  {run.Driver}  " +
            $"up {up.TotalSeconds:0.0}s  total {clock.Elapsed.TotalSeconds:0.0}s  nothing left behind");
    }

    Console.WriteLine($"Soak passed: {rounds} round(s) of up, reset and down.");
    return 0;
}

static async Task<int> Check(DockerEngine docker, LabOrchestrator lab, string[] options)
{
    var scenario = Scenario.Load(Scenario.Locate(SentinelHill.Scenario));
    var waves = scenario.TrafficWaves.ToList();
    if (options.Length == 0 || !int.TryParse(options[0], out var wave) || !waves.Contains(wave))
    {
        Console.Error.WriteLine($"Usage: check <wave>, where wave is one of {string.Join(", ", waves)}.");
        return 2;
    }

    var runs = await lab.ListAsync();
    if (runs.Count == 0)
    {
        Console.Error.WriteLine("No lab is running. Use: up");
        return 1;
    }

    var run = runs[0];
    Console.WriteLine($"Gate: {(await GateState.ReadAsync(docker, run)).Describe()}");
    var verdict = await scenario.JudgeAsync(wave, new CheckContext(docker, run));
    foreach (var result in verdict.Outcomes.Select(o => o.Result))
        Console.WriteLine($"  {(result.Passed ? "PASS" : "FAIL")}  {result.Id}: {result.Detail}");
    Console.WriteLine($"Wave {wave}: {(verdict.Passed ? "held" : "not held")}");
    return verdict.Passed ? 0 : 1;
}

static async Task<int> Play(DockerEngine docker, LabOrchestrator lab, string[] options)
{
    int Option(string name, int fallback)
    {
        var at = Array.IndexOf(options, name);
        return at >= 0 && at + 1 < options.Length && int.TryParse(options[at + 1], out var value) ? value : fallback;
    }

    var themeAt = Array.IndexOf(options, "--theme");
    var theme = Theme.Current(themeAt >= 0 && themeAt + 1 < options.Length ? options[themeAt + 1] : null);
    var scenario = theme.Apply(Scenario.Load(Scenario.Locate(SentinelHill.Scenario)));
    var history = HistoryStore.Default();
    var firstWave = Option("--wave", history.SuggestedWave(scenario));
    scenario.Wave(firstWave);

    var existing = await lab.ListAsync();
    LabRun run;
    string how;
    if (existing.Count == 0)
    {
        run = await lab.UpAsync(SentinelHill.Create(Option("--seed", Random.Shared.Next())));
        how = "new";
    }
    else if (existing[0].IsRunning)
    {
        run = existing[0];
        how = "already running";
    }
    else
    {
        Console.WriteLine($"Run {existing[0].RunId} is stopped, probably because Docker restarted. Rebuilding it.");
        run = await lab.ResetAsync(existing[0]);
        how = "rebuilt";
    }

    Console.WriteLine($"{scenario.Title}  (run {run.RunId}, {how})");
    Console.WriteLine();
    Console.WriteLine("Open a second terminal and step into the gate:");
    Console.WriteLine($"  docker exec -it {run.ContainerName("gate")} bash");
    Console.WriteLine("Then type: quest");
    Console.WriteLine();
    Console.WriteLine("This terminal is the game master. Leave it running. Ctrl+C ends the game.");
    Console.WriteLine("Do not play from the web page at the same time: there can only be one game master.");
    Console.WriteLine($"Starting at wave {firstWave}. Theme: {theme.Name}.");
    Console.WriteLine();

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };

    var recorder = new GameRecorder(history, scenario, theme, run.Spec.Seed, firstWave);
    var session = new GameSession(docker, run, scenario, line => Console.WriteLine($"  {line}"), recorder.On);
    try
    {
        var outcomes = await session.RunAsync(firstWave, stop.Token);
        Console.WriteLine();
        foreach (var o in outcomes)
            Console.WriteLine($"  Wave {o.Wave}  {o.Title,-16} {o.Xp,4} XP  attempts {o.Attempts}  hints {o.HintsUsed}{(o.Mastered ? "  mastered" : "")}");
        Console.WriteLine($"  Total {outcomes.Sum(o => o.Xp)} XP");
    }
    catch (OperationCanceledException)
    {
        await session.CloseAsync();
        Console.WriteLine();
        Console.WriteLine("Game ended. Your rules in the gate are still there.");
    }
    recorder.End();

    Console.WriteLine("The lab is still running. Remove it with: down");
    return 0;
}

static int History()
{
    var history = HistoryStore.Default();
    var scenario = Theme.Current().Apply(Scenario.Load(Scenario.Locate(SentinelHill.Scenario)));
    var games = history.Games.Where(g => g.Scenario == scenario.Id).ToList();
    Console.WriteLine($"{scenario.Title}: {games.Count} game(s), {games.Count(g => g.Completed)} completed. Kept in {history.Path}");
    Console.WriteLine();
    foreach (var p in history.Progress(scenario))
    {
        var status = p.Mastered > 0 ? $"mastered {p.Mastered}x"
            : p.Held > 0 ? $"held {p.Held}x, {p.HintsUsed} hint(s)"
            : p.Played > 0 ? $"not held yet, {p.FailedAttempts} {(p.FailedAttempts == 1 ? "try" : "tries")}"
            : "not played";
        Console.WriteLine($"  Wave {p.Wave}  {p.Skill,-58} {(p.Held > 0 ? $"best {p.BestXp} XP" : ""),-13} {status}");
        if (p.Mastered == 0 && p.StuckOn is { } stuck) Console.WriteLine($"          fell most on: {stuck}");
    }
    Console.WriteLine();
    Console.WriteLine($"Practise next: wave {history.SuggestedWave(scenario)}");
    return 0;
}

static void Print(LabRun run)
{
    Console.WriteLine($"Run {run.RunId}  scenario {run.Spec.Scenario}  seed {run.Spec.Seed}  networks {run.Driver}");
    if (!run.IsRunning)
        Console.WriteLine("STOPPED: not every container is running, probably because Docker restarted. Rebuild it with: reset");
    Console.WriteLine($"Expires {run.Expires.ToLocalTime():yyyy-MM-dd HH:mm}");
    foreach (var network in run.Spec.Networks)
        Console.WriteLine($"  network {network.Key,-9} {network.Subnet}");
    foreach (var node in run.Spec.Nodes)
    {
        var state = run.Nodes.FirstOrDefault(n => n.Role == node.Role)?.State ?? "missing";
        var addresses = string.Join(", ", node.Attachments.Select(a => a.Address));
        Console.WriteLine($"  {node.Role,-9} {state,-8} {addresses}");
    }
    Console.WriteLine($"  web ports: {string.Join(", ", run.Spec.Parameters.Select(p => $"{p.Key[4..]} {p.Value}"))}");
    Console.WriteLine($"Shell in the gate: docker exec -it {run.ContainerName("gate")} bash");
}
