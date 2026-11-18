using Game.Core;
using Game.Orchestration;

/// <summary>
/// The test of the themes: every theme in themes/ loads and applies to the
/// scenario, the neutral theme leaves it as written, and a theme that would
/// change what the player must do is refused.
/// </summary>
internal static class ThemeTest
{
    public static int Run()
    {
        var failures = 0;
        void Expect(string what, bool ok, string shown = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(!ok && shown.Length > 0 ? $" ({shown})" : "")}");
        }

        var scenario = Scenario.Load(Scenario.Locate(SentinelHill.Scenario));

        foreach (var id in Theme.Available())
        {
            try
            {
                var theme = Theme.Load(id);
                var themed = theme.Apply(scenario);
                var checks = themed.Waves.SelectMany(w => w.Checks ?? []).ToList();
                var same = themed.Waves.Zip(scenario.Waves).All(p =>
                    p.First.Number == p.Second.Number && p.First.Kind == p.Second.Kind && p.First.Xp == p.Second.Xp
                    && (p.First.Checks ?? []).Select(c => c with { Title = "" })
                        .SequenceEqual((p.Second.Checks ?? []).Select(c => c with { Title = "" }))
                    && p.First.Hints.SequenceEqual(p.Second.Hints));
                Expect($"theme {id} ({theme.Name}) loads and leaves waves, checks and hints alone", same);
                Expect($"theme {id} names every place and person on the map",
                    Theme.NeutralNames.Keys.All(k => theme.Label(k).Length > 0));
            }
            catch (ScenarioException e)
            {
                Expect($"theme {id} loads", false, e.Message);
            }
        }

        var neutral = Theme.Load("neutral").Apply(scenario);
        Expect("the neutral theme leaves every text as written",
            neutral.Title == scenario.Title
            && neutral.Waves.Zip(scenario.Waves).All(p => p.First.Briefing.SequenceEqual(p.Second.Briefing) && p.First.Title == p.Second.Title)
            && Theme.Load("neutral").Label("citizens") == "Citizens");

        var wow = Theme.Load("wow").Apply(scenario);
        Expect("the wow theme gives its own title, briefings and names",
            wow.Title != scenario.Title && !wow.Wave(1).Briefing.SequenceEqual(scenario.Wave(1).Briefing)
            && Theme.Load("wow").Label("raiders") == "Defias");

        Refused("a briefing without the address of the board", w => w.Select(l => l.Replace("{web}", "the board")).ToArray(), "1");
        Refused("a briefing without the report command", w => w.Where(l => !l.Contains("quest report")).ToArray());
        Refused("a wave the scenario does not have", null, wave: "9");

        Console.WriteLine(failures == 0 ? "Theme test passed." : $"Theme test FAILED: {failures} check(s).");
        return failures == 0 ? 0 : 1;

        void Refused(string what, Func<string[], string[]>? change, string wave = "0")
        {
            var briefing = change?.Invoke(scenario.Wave(0).Briefing);
            var bad = new Theme("bad", "Bad", null, new Dictionary<string, ScenarioText>
            {
                [scenario.Id] = new(Waves: new Dictionary<string, WaveText> { [wave] = new(Briefing: briefing) }),
            });
            try
            {
                bad.Apply(scenario);
                Expect($"refused: {what}", false, "it was accepted");
            }
            catch (ScenarioException)
            {
                Expect($"refused: {what}", true);
            }
        }
    }
}
