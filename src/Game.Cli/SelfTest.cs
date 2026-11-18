using Game.Checks;
using Game.Core;
using Game.Orchestration;

/// <summary>
/// The test of the checks themselves. For every wave it loads known defences
/// into the gate, at least one right and three wrong, and requires the verdict
/// to match. A check that passes a wrong defence teaches the wrong thing.
/// </summary>
internal static class SelfTest
{
    private sealed record Defence(int Wave, string Name, bool ShouldPass, string Rules);

    private const string Open = "table inet gate {\n  chain forward {\n    type filter hook forward priority 0; ";
    private const string Close = "\n  }\n}\n";
    private const string Established = "ct state established,related accept";
    private const string Board = "ip daddr {WEB} tcp dport {HTTP} accept";
    private const string Management = "ip saddr {GARRISON} ip daddr {WEB} tcp dport {MGMT} accept";

    private static string Deny(params string[] rules) =>
        Open + "policy drop;\n    " + string.Join("\n    ", rules) + Close;

    private static string PerSource(int rate, params string[] rules) =>
        "table inet gate {\n  set flood {\n    type ipv4_addr\n    flags dynamic\n    timeout 30s\n  }\n" +
        "  chain forward {\n    type filter hook forward priority 0; policy drop;\n    " +
        string.Join("\n    ", rules.Prepend(
            $"ip daddr {{WEB}} tcp dport {{HTTP}} ct state new add @flood {{ ip saddr limit rate over {rate}/second burst {rate} packets }} drop")
            .Prepend(Established)) + Close;

    private static readonly Defence[] Defences =
    [
        new(6, "default deny, board open", true, Deny(Established, Board)),
        new(6, "policy accept with explicit drops", true,
            Open + "policy accept;\n    ip daddr {WEB} tcp dport != {HTTP} ct state new drop" + Close),
        new(6, "no rules at all", false, ""),
        new(6, "everything blocked", false, Deny()),
        new(6, "no rule for established flows", false, Deny(Board)),
        new(6, "debug page left open", false, Deny(Established, "ip daddr {WEB} tcp dport { {HTTP}, {DEBUG} } accept")),

        new(7, "management from garrison only", true, Deny(Established, Management, Board)),
        new(7, "management not opened", false, Deny(Established, Board)),
        new(7, "management open to everyone", false,
            Deny(Established, "ip daddr {WEB} tcp dport {MGMT} accept", Board)),
        new(7, "management open to the whole plains", false,
            Deny(Established, "ip saddr {PLAINS} ip daddr {WEB} tcp dport {MGMT} accept", Board)),
        new(7, "management right, board closed", false, Deny(Established, Management)),

        new(8, "limit per source address", true, PerSource(10, Management, Board)),
        new(8, "no rate limit", false, Deny(Established, Management, Board)),
        new(8, "block list with one address", false,
            Deny(Established, "ip saddr {DEFIAS} drop", Management, Board)),
        new(8, "one limit shared by everyone", false,
            Deny(Established, Management,
                "ip daddr {WEB} tcp dport {HTTP} ct state new limit rate over 15/second burst 5 packets drop", Board)),
        new(8, "limit per source far too generous", false, PerSource(100, Management, Board)),
    ];

    /// <summary>A defence known to hold the given wave, for tests that need to get past it.</summary>
    public static string RightDefence(int wave) => Defences.First(d => d.Wave == wave && d.ShouldPass).Rules;

    /// <summary>A defence that drops by default but forgets the replies.</summary>
    public static string NoEstablishedDefence => Defences.First(d => d.Name == "no rule for established flows").Rules;

    public static async Task<int> RunAsync(DockerEngine docker, LabOrchestrator lab)
    {
        var scenario = Scenario.Load(Scenario.Locate(SentinelHill.Scenario));
        if ((await lab.ListAsync()).Count > 0)
        {
            Console.Error.WriteLine("A lab is already running. Remove it first: down");
            return 1;
        }

        var run = await lab.UpAsync(SentinelHill.Create(Random.Shared.Next()));
        Console.WriteLine($"Run {run.RunId}  seed {run.Spec.Seed}  networks {run.Driver}");
        var context = new CheckContext(docker, run);
        var wrong = 0;

        try
        {
            foreach (var defence in Defences)
            {
                await LoadAsync(docker, run, defence.Rules);
                var verdict = await scenario.JudgeAsync(defence.Wave, context);
                var agrees = verdict.Passed == defence.ShouldPass;
                if (!agrees) wrong++;

                Console.WriteLine(
                    $"{(agrees ? "ok   " : "WRONG")}  wave {defence.Wave}  " +
                    $"{(defence.ShouldPass ? "right" : "wrong")} defence judged " +
                    $"{(verdict.Passed ? "PASS" : "FAIL")}  {defence.Name}");
                foreach (var result in verdict.Outcomes.Select(o => o.Result).Where(r => !r.Passed || !agrees))
                    Console.WriteLine($"         {(result.Passed ? "pass" : "fail")}  {result.Id}: {result.Detail}");
            }

            // The answers of cheat mode, run exactly as the player would paste them.
            foreach (var answer in Answers.For(scenario, run.Spec).Where(a => scenario.Wave(a.Wave).Kind == WaveSpec.Traffic))
            {
                await docker.ExecAsync(run.ContainerName("gate"), ["nft", "flush", "ruleset"]);
                var pasted = await docker.ExecAsync(run.ContainerName("gate"), ["sh", "-c", answer.Steps]);
                var verdict = await scenario.JudgeAsync(answer.Wave, context);
                var agrees = pasted.Succeeded && verdict.Passed;
                if (!agrees) wrong++;
                Console.WriteLine($"{(agrees ? "ok   " : "WRONG")}  wave {answer.Wave}  cheat-mode answer judged {(verdict.Passed ? "PASS" : "FAIL")}");
                if (!pasted.Succeeded) Console.WriteLine($"         the answer did not run: {pasted.Output.Trim()}");
                foreach (var result in verdict.Outcomes.Select(o => o.Result).Where(r => !r.Passed))
                    Console.WriteLine($"         fail  {result.Id}: {result.Detail}");
            }
        }
        finally
        {
            await lab.DownAsync(run.RunId);
        }

        var total = Defences.Length + scenario.TrafficWaves.Count();
        Console.WriteLine(wrong == 0
            ? $"Self-test passed: {total} defences judged as expected."
            : $"Self-test FAILED: {wrong} of {total} defences judged wrongly.");
        return wrong == 0 ? 0 : 1;
    }

    public static async Task LoadAsync(DockerEngine docker, LabRun run, string rules)
    {
        var context = new CheckContext(docker, run);
        var text = "flush ruleset\n" + rules
            .Replace("{WEB}", context.WebAddress)
            .Replace("{GARRISON}", context.AddressOf("garrison"))
            .Replace("{DEFIAS}", context.AddressOf("defias"))
            .Replace("{PLAINS}", run.Spec.Networks.First(n => n.Key == SentinelHill.Plains).Subnet)
            .Replace("{HTTP}", context.Port("http").ToString())
            .Replace("{MGMT}", context.Port("management").ToString())
            .Replace("{DEBUG}", context.Port("debug").ToString());

        var result = await docker.ExecAsync(
            run.ContainerName("gate"),
            ["sh", "-c", $"cat > /tmp/defence.nft <<'RULES'\n{text}\nRULES\nnft -f /tmp/defence.nft"]);
        if (!result.Succeeded)
            throw new LabException($"nft rejected a test defence: {result.Output.Trim()}\n{text}");
    }
}
