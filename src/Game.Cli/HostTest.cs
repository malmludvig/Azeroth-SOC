using System.Diagnostics;
using Game.Core;
using Game.Orchestration;

/// <summary>
/// The test of the game host behind the web page: starts a game the way the
/// page's button does, plays it through quest in the gate, and requires the
/// view the page draws from (phase, wave, log, meters, doors) to follow.
/// </summary>
internal static class HostTest
{
    public static async Task<int> RunAsync(DockerEngine docker, LabOrchestrator lab)
    {
        if ((await lab.ListAsync()).Count > 0)
        {
            Console.Error.WriteLine("A lab is already running. Remove it first: down");
            return 1;
        }

        var failures = 0;
        void Expect(string what, bool ok, string shown = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok && shown.Length > 0) Console.WriteLine("        got: " + shown.Trim().Replace("\n", "\n             "));
        }

        var changes = 0;
        var historyPath = Path.Combine(Path.GetTempPath(), $"azeroth-hosttest-{Guid.NewGuid():N}.json");
        var history = new HistoryStore(historyPath);
        using var host = new GameHost(docker, lab, history, Theme.Load("wow"));
        host.Changed += () => Interlocked.Increment(ref changes);

        async Task<bool> WaitFor(Func<GameView, bool> condition, int seconds = 20)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (condition(host.View)) return true;
                await Task.Delay(200);
            }
            return condition(host.View);
        }

        Expect("before the start the host is idle", host.View.Phase == GamePhase.Idle);
        await host.StartAsync();
        var view = host.View;
        Expect("the start builds a lab and the game is playing", view.Phase == GamePhase.Playing && view.RunId is not null, view.Phase.ToString());

        var run = (await lab.ListAsync()).FirstOrDefault();
        if (run is null)
        {
            Expect("a lab exists", false);
            return 1;
        }

        async Task<string> Quest(string arguments = "") =>
            (await docker.ExecAsync(run.ContainerName("gate"), ["sh", "-c", $"quest {arguments}"])).Output;

        try
        {
            Expect("wave 0 is shown with its briefing", await WaitFor(v => v.CurrentWave == 0 && v.Briefing?.Contains("Wave 0 of 3") == true));
            Expect("the waves of the scenario are listed", host.View.Waves.Count == 4);
            Expect("the game is in the host's theme", host.View.ScenarioTitle == host.LoadScenario().Title
                && host.View.Briefing?.Contains("Gryan") == true, host.View.ScenarioTitle);
            await Task.Delay(3500);
            Expect("in wave 0 no doors are shown: finding them is the task", host.View.Doors.Count == 0);
            Expect("the gate's state is shown", await WaitFor(v => v.GateState is not null, 8), host.View.GateState ?? "");

            await Quest("hint");
            Expect("a hint shows up in the log", await WaitFor(v => v.Log.Any(e => e.Kind == "hint")));

            var spec = run.Spec;
            var plains = spec.Networks.First(n => n.Key == SentinelHill.Plains).Subnet;
            var dmz = spec.Networks.First(n => n.Key == SentinelHill.Dmz).Subnet;
            await Quest($"report plains={plains} dmz={dmz} ports={string.Join(",", spec.Parameters.Values)}");
            Expect("the report moves the game to wave 1", await WaitFor(v => v.CurrentWave == 1));
            Expect("the held wave is counted with its XP", host.View.Outcomes is [{ Wave: 0, Xp: 30 }],
                string.Join(", ", host.View.Outcomes));

            Expect("in wave 1 the doors appear, all reachable with no rules",
                await WaitFor(v => v.Doors.Count == 4 && v.Doors.All(d => d.OutsidersReach == true)),
                string.Join(", ", host.View.Doors));

            var bell = Quest("ready");
            Expect("the bell switches the game to judging", await WaitFor(v => v.Phase == GamePhase.Judging, 10));
            await bell;
            await WaitFor(v => v.Phase == GamePhase.Playing);
            view = host.View;
            Expect("a gate without rules is not held, with three intrusions and the citizens served",
                view.Log.Last().Kind == "fell" && view.Intrusions == 3 && view.Health == 1.0,
                $"{view.Log.Last().Kind} intrusions={view.Intrusions} health={view.Health}");

            await SelfTest.LoadAsync(docker, run, SelfTest.RightDefence(1));
            Expect("the doors follow the new rules: only the board stays reachable",
                await WaitFor(v => v.Doors.Count == 4 && v.Doors.All(d => d.OutsidersReach == (d.Name == "http"))),
                string.Join(", ", host.View.Doors));

            await Quest("ready");
            Expect("the right defence holds wave 1 and the game moves on", await WaitFor(v => v.CurrentWave == 2));
            view = host.View;
            Expect("after the held wave the meters show a clean gate", view.Intrusions is null || view.Intrusions == 0,
                $"intrusions={view.Intrusions}");
            Expect("the total XP is 30 + 90", view.TotalXp == 120, view.TotalXp.ToString());

            Expect("the page was told about every change", changes > 10, changes.ToString());

            await host.StopAsync();
            var saved = new HistoryStore(historyPath).Games;
            Expect("the game is kept in the history, with both held waves and the failed attempt",
                saved is [{ Theme: "wow", Completed: false, Ended: not null } game]
                && game.Waves.Select(w => w.Wave).SequenceEqual([0, 1])
                && game.Attempts.Count(a => !a.Held) == 1 && game.Hints.GetValueOrDefault(0) == 1,
                saved.Count.ToString());
            var progress = history.Progress(host.LoadScenario());
            Expect("the progress shows wave 1 mastered, wave 0 held with a hint, and what wave 1 fell on",
                progress[0] is { Held: 1, Mastered: 0, HintsUsed: 1 } && progress[1] is { Mastered: 1 }
                && progress[1].StuckOn?.Contains("Defias scouts") == true,
                string.Join(" | ", progress));
            Expect("the next wave to practise is wave 0, the first not mastered", history.SuggestedWave(host.LoadScenario()) == 0);
            Expect("stopping ends the game but keeps the lab",
                host.View.Phase == GamePhase.Idle && (await lab.FindAsync(run.RunId)) is { IsRunning: true });
            Expect("after stopping, quest says no game is running", (await Quest()).Contains("No game is running"));

            await host.StartAsync(firstWave: 2);
            Expect("a new game reuses the running lab and can start at a later wave",
                await WaitFor(v => v.CurrentWave == 2) && host.View.RunId == run.RunId);
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync();
            await lab.DownAsync(run.RunId);
            File.Delete(historyPath);
        }

        Console.WriteLine(failures == 0 ? "Host test passed." : $"Host test FAILED: {failures} check(s).");
        return failures == 0 ? 0 : 1;
    }
}
