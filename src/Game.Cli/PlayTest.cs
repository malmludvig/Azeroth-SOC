using Game.Core;
using Game.Orchestration;

/// <summary>
/// The test of the scenario engine: plays the whole scenario the way a player
/// would, through the quest command in the gate, with wrong tries and hints on
/// the way, and requires the expected replies and the expected score.
/// </summary>
internal static class PlayTest
{
    public static async Task<int> RunAsync(DockerEngine docker, LabOrchestrator lab)
    {
        if ((await lab.ListAsync()).Count > 0)
        {
            Console.Error.WriteLine("A lab is already running. Remove it first: down");
            return 1;
        }

        var scenario = Scenario.Load(Scenario.Locate(SentinelHill.Scenario));
        var run = await lab.UpAsync(SentinelHill.Create(Random.Shared.Next()));
        Console.WriteLine($"Run {run.RunId}  seed {run.Spec.Seed}  networks {run.Driver}");

        var failures = 0;
        void Expect(string what, bool ok, string shown = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok && shown.Length > 0) Console.WriteLine("        got: " + shown.Trim().Replace("\n", "\n             "));
        }

        async Task<string> Quest(string arguments = "") =>
            (await docker.ExecAsync(run.ContainerName("gate"), ["sh", "-c", $"quest {arguments}"])).Output;

        using var stop = new CancellationTokenSource();
        var session = new GameSession(docker, run, scenario);
        var game = session.RunAsync(ct: stop.Token);

        try
        {
            var spec = run.Spec;
            var plains = spec.Networks.First(n => n.Key == SentinelHill.Plains).Subnet;
            var dmz = spec.Networks.First(n => n.Key == SentinelHill.Dmz).Subnet;
            var ports = string.Join(",", spec.Parameters.Values);
            var web = spec.Node("web").AddressOn(SentinelHill.Dmz);

            // The game master publishes the first mission a moment after it starts.
            var text = "";
            for (var i = 0; i < 20 && !text.Contains("Wave 0"); i++)
            {
                await Task.Delay(300);
                if (game.IsFaulted) await game;
                text = await Quest();
            }
            Expect("the first mission is shown and names the notice board", text.Contains("Wave 0") && text.Contains(web), text);
            Expect("the first mission does not give the networks away", !text.Contains(plains) && !text.Contains(dmz), text);

            text = await Quest("ready");
            Expect("wave 0 cannot be settled with the bell", text.Contains("report"), text);
            text = await Quest($"report plains=10.1.2.0/24 dmz={dmz} ports=80");
            Expect("a wrong report is refused and says what is wrong", text.Contains("Not right yet: plains, ports (1 of 4 found)"), text);
            Expect("a wrong report does not reveal the answer", !text.Contains(plains), text);
            text = await Quest("hint");
            Expect("a hint is given at level 1", text.Contains("Hint 1 of 3"), text);
            text = await Quest($"report plains={plains} dmz={dmz} ports={ports}");
            Expect("the right report holds wave 0 for 20 XP", text.Contains("Wave 0 is held. +20 XP"), text);

            text = await Quest();
            Expect("the next mission is wave 1", text.Contains("Wave 1"), text);
            await SelfTest.LoadAsync(docker, run, SelfTest.NoEstablishedDefence);
            await Quest("hint");
            text = await Quest("hint");
            Expect("the level 2 hint fits the gate: it talks about replies", text.Contains("Hint 2 of 3") && text.Contains("ct state"), text);
            text = await Quest("ready");
            Expect("a wrong defence does not hold wave 1", text.Contains("FELL") && text.Contains("Not held yet"), text);
            await SelfTest.LoadAsync(docker, run, SelfTest.RightDefence(1));
            text = await Quest("ready");
            Expect("the right defence holds wave 1 for 50 XP", text.Contains("Wave 1 is held. +50 XP"), text);

            text = await Quest("ready");
            Expect("the wave 1 defence does not hold wave 2", text.Contains("FELL") && text.Contains("garrison"), text);
            await SelfTest.LoadAsync(docker, run, SelfTest.RightDefence(2));
            text = await Quest("ready");
            Expect("the right defence holds wave 2 for 90 XP", text.Contains("Wave 2 is held. +90 XP"), text);

            await SelfTest.LoadAsync(docker, run, SelfTest.RightDefence(3));
            text = await Quest("ready");
            Expect("the right defence holds wave 3, mastered", text.Contains("Wave 3 is held. +100 XP, and mastered"), text);
            Expect("the ending shows the total", text.Contains("Total 260 XP, 2 of 4 waves mastered"), text);

            var outcomes = await game.WaitAsync(TimeSpan.FromSeconds(10));
            Expect("the game ends with four waves and 260 XP", outcomes.Count == 4 && outcomes.Sum(o => o.Xp) == 260,
                string.Join(", ", outcomes.Select(o => $"wave {o.Wave}: {o.Xp}")));

            text = await Quest();
            Expect("afterwards quest shows the ending", text.Contains("complete"), text);
        }
        finally
        {
            stop.Cancel();
            await lab.DownAsync(run.RunId);
        }

        Console.WriteLine(failures == 0 ? "Play test passed." : $"Play test FAILED: {failures} check(s).");
        return failures == 0 ? 0 : 1;
    }
}
