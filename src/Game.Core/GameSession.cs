using System.Text;
using Game.Checks;
using Game.Orchestration;

namespace Game.Core;

/// <summary>How a wave went. Mastered means held without any hint.</summary>
public sealed record WaveOutcome(int Wave, string Title, int Attempts, int HintsUsed, int Xp, bool Mastered);

/// <summary>
/// Plays a scenario against a running lab, wave by wave: publish the mission,
/// answer the player's quest commands, judge when they ring the bell, and move
/// on when the wave is held. The player works in the gate the whole time.
/// </summary>
public sealed class GameSession(DockerEngine docker, LabRun run, Scenario scenario, Action<string>? log = null)
{
    public const int HintCost = 20;
    public const int RetryCost = 10;
    public const int MinimumXp = 10;
    public const int HintLevels = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Action<string> _log = log ?? (_ => { });
    private readonly GateMailbox _mailbox = new(docker, run);
    private readonly CheckContext _context = new(docker, run);

    public async Task<IReadOnlyList<WaveOutcome>> RunAsync(int firstWave = 0, CancellationToken ct = default)
    {
        var waves = scenario.Waves.Where(w => w.Number >= firstWave).OrderBy(w => w.Number).ToList();
        if (waves.Count == 0) throw new ScenarioException($"Scenario {scenario.Id} has no wave {firstWave} or later.");

        var outcomes = new List<WaveOutcome>();
        await _mailbox.OpenAsync(ct);

        for (var i = 0; i < waves.Count; i++)
        {
            var wave = waves[i];
            await _mailbox.PublishAsync(Briefing(wave), wave.Kind, ct);
            _log($"Wave {wave.Number}: {wave.Title}. Waiting for the player.");

            var (outcome, request, verdictText) = await PlayWaveAsync(wave, ct);
            outcomes.Add(outcome);

            // The next mission is in place before the player is told this one is
            // over, so that "quest" right afterwards shows where to go next.
            var last = i == waves.Count - 1;
            if (last) await _mailbox.PublishAsync(Ending(outcomes), "over", ct);
            else await _mailbox.PublishAsync(Briefing(waves[i + 1]), waves[i + 1].Kind, ct);

            var reply = new StringBuilder(verdictText);
            reply.AppendLine().AppendLine($"Wave {wave.Number} is held. +{outcome.Xp} XP" +
                (outcome.Mastered ? ", and mastered: no hints used." : "."));
            reply.AppendLine().AppendLine("To think about: " + wave.Question);
            reply.AppendLine().Append(last ? Ending(outcomes) : "The next mission is waiting: quest");
            await _mailbox.ReplyAsync(request, reply.ToString(), ct);

            _log($"Wave {wave.Number} held after {outcome.Attempts} attempt(s) and {outcome.HintsUsed} hint(s): +{outcome.Xp} XP");
        }

        return outcomes;
    }

    /// <summary>Stops answering quest. Call when the session ends early.</summary>
    public Task CloseAsync() => _mailbox.CloseAsync();

    private async Task<(WaveOutcome Outcome, QuestRequest Request, string Verdict)> PlayWaveAsync(
        WaveSpec wave, CancellationToken ct)
    {
        var attempts = 0;
        var hints = 0;

        while (true)
        {
            var request = await _mailbox.PollAsync(ct);
            if (request is null)
            {
                await Task.Delay(PollInterval, ct);
                continue;
            }

            string verdict;
            bool held;
            switch (request.Verb)
            {
                case "hint":
                    hints = Math.Min(hints + 1, HintLevels);
                    _log($"Wave {wave.Number}: hint {hints} asked for");
                    await _mailbox.ReplyAsync(request, await HintAsync(wave, hints, ct), ct);
                    continue;

                case "report" when wave.Kind == WaveSpec.Report:
                    attempts++;
                    (held, verdict) = JudgeReport(request.Arguments);
                    break;

                case "ready" when wave.Kind == WaveSpec.Traffic:
                    attempts++;
                    _log($"Wave {wave.Number}: the bell rings, attempt {attempts}");
                    (held, verdict) = await JudgeTrafficAsync(wave, ct);
                    break;

                default:
                    await _mailbox.ReplyAsync(request, wave.Kind == WaveSpec.Report
                        ? "This mission is settled with a report: quest report ..."
                        : "This mission is settled at the gate: write your rules, then quest ready", ct);
                    continue;
            }

            if (held)
            {
                var xp = Math.Max(MinimumXp, wave.Xp - HintCost * hints - RetryCost * (attempts - 1));
                return (new WaveOutcome(wave.Number, wave.Title, attempts, hints, xp, hints == 0), request, verdict);
            }

            _log($"Wave {wave.Number}: not held on attempt {attempts}");
            await _mailbox.ReplyAsync(request, verdict + "\nNot held yet. Adjust and try again, or ask: quest hint", ct);
        }
    }

    private async Task<(bool Held, string Text)> JudgeTrafficAsync(WaveSpec wave, CancellationToken ct)
    {
        var verdict = await scenario.JudgeAsync(wave.Number, _context, ct);
        var text = new StringBuilder();
        foreach (var outcome in verdict.Outcomes)
        {
            text.AppendLine($"  [{(outcome.Result.Passed ? "held" : "FELL")}] {outcome.Spec.Title}");
            if (!outcome.Result.Passed) text.AppendLine($"         {outcome.Result.Detail}");
        }
        text.AppendLine($"The gate now: {(await GateState.ReadAsync(docker, run, ct)).Describe()}");
        return (verdict.Passed, text.ToString());
    }

    // The report says which parts are wrong, never what the right answer is.
    private (bool Held, string Text) JudgeReport(string[] arguments)
    {
        var given = arguments
            .Select(a => a.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());

        var spec = run.Spec;
        var truePorts = new[] { "http", "management", "debug", "database" }.Select(_context.Port).ToHashSet();
        var givenPorts = given.GetValueOrDefault("ports", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var port) ? port : -1)
            .ToHashSet();

        var wrong = new List<string>();
        if (given.GetValueOrDefault("plains") != Subnet(SentinelHill.Plains)) wrong.Add("plains");
        if (given.GetValueOrDefault("dmz") != Subnet(SentinelHill.Dmz)) wrong.Add("dmz");
        if (!givenPorts.SetEquals(truePorts))
        {
            var found = givenPorts.Count(truePorts.Contains);
            var extra = givenPorts.Count - found;
            wrong.Add($"ports ({found} of {truePorts.Count} found" + (extra > 0 ? $", {extra} that do not answer" : "") + ")");
        }

        return wrong.Count == 0
            ? (true, "  [held] Your map of the gate is right.\n")
            : (false, $"  Not right yet: {string.Join(", ", wrong)}.\n");

        string Subnet(string key) => spec.Networks.First(n => n.Key == key).Subnet;
    }

    private async Task<string> HintAsync(WaveSpec wave, int level, CancellationToken ct)
    {
        var state = await GateState.ReadAsync(docker, run, ct);
        var hint = wave.Hints.Where(h => h.Level == level).First(h => Applies(h.When, state));
        return $"Hint {level} of {HintLevels} (each level costs {HintCost} XP):\n\n{Render(hint.Text)}\n";
    }

    private static bool Applies(string condition, GateState state) => condition switch
    {
        "no-forward-chain" => !state.HasForwardChain,
        "policy-accept" => state.HasForwardChain && state.ForwardPolicy != "drop",
        "no-established" => state.HasForwardChain && state.ForwardPolicy == "drop" && !state.AcceptsEstablished,
        "no-rate-limit" => !state.UsesRateLimit,
        _ => true,
    };

    private string Briefing(WaveSpec wave)
    {
        var last = scenario.Waves.Max(w => w.Number);
        var text = new StringBuilder();
        text.AppendLine($"== Wave {wave.Number} of {last}: {wave.Title} ==").AppendLine();
        if (wave.Number == scenario.Waves.Min(w => w.Number)) text.AppendLine(Render(scenario.Intro)).AppendLine();
        text.AppendLine(Render(wave.Briefing)).AppendLine();
        text.Append("Stuck? quest hint");
        return text.ToString();
    }

    private string Ending(IReadOnlyList<WaveOutcome> outcomes)
    {
        var text = new StringBuilder();
        text.AppendLine($"== {scenario.Title}: complete ==").AppendLine();
        text.AppendLine(Render(scenario.Outro)).AppendLine();
        foreach (var o in outcomes)
        {
            text.AppendLine(
                $"  Wave {o.Wave}  {o.Title,-16} {o.Xp,4} XP  attempts {o.Attempts}  hints {o.HintsUsed}" +
                (o.Mastered ? "  mastered" : ""));
        }
        text.Append($"  Total {outcomes.Sum(o => o.Xp)} XP, {outcomes.Count(o => o.Mastered)} of {outcomes.Count} waves mastered");
        return text.ToString();
    }

    // Addresses and ports change with every run, so the text names them by role.
    private string Render(string[] lines) => string.Join('\n', lines)
        .Replace("{web}", _context.WebAddress)
        .Replace("{garrison}", _context.AddressOf("garrison"))
        .Replace("{http}", _context.Port("http").ToString())
        .Replace("{management}", _context.Port("management").ToString());
}
