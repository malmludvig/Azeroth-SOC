using System.Diagnostics;
using System.Globalization;

namespace Game.Checks;

/// <summary>
/// Probe: connects from one host to a set of ports on the web server and
/// compares what answers with what should.
/// </summary>
public sealed class PortsCheck(
    string id, string fromRole, IReadOnlyList<string> expectOpen, IReadOnlyList<string> expectClosed) : ICheck
{
    public string Id => id;

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken ct = default)
    {
        var names = expectOpen.Concat(expectClosed).ToList();
        var open = await Task.WhenAll(names.Select(n => context.CanConnectAsync(fromRole, context.Port(n), ct)));

        var wrong = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var shouldBeOpen = i < expectOpen.Count;
            if (open[i] == shouldBeOpen) continue;
            wrong.Add($"{names[i]} ({context.Port(names[i])}) is {(open[i] ? "reachable" : "blocked")}");
        }

        return wrong.Count == 0
            ? new CheckResult(id, true, $"from {fromRole}: as expected")
            : new CheckResult(id, false, $"from {fromRole}: {string.Join(", ", wrong)}");
    }
}

/// <summary>
/// Measurement on the sender's side: the share of ordinary web requests from
/// one host that get an answer. This is the health of the town.
/// </summary>
public sealed class SuccessRateCheck(string id, string fromRole, int requests, double minimum) : ICheck
{
    public string Id => id;

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken ct = default)
    {
        var served = await Traffic.OrdinaryRequestsAsync(context, fromRole, requests, ct);
        var rate = (double)served / requests;
        return new CheckResult(id, rate >= minimum,
            $"{fromRole}: {served}/{requests} requests answered (needs {minimum:P0})");
    }
}

/// <summary>
/// Measurement during a flood. Each attacker floods in turn while the citizens
/// keep browsing. The attacker's rate is counted in the web server's access
/// log, after the first second so that a burst allowance is not held against
/// the defence. Several attackers in turn is what makes a hand-written block
/// list fail: the second address is not on it.
/// </summary>
public sealed class FloodCheck(
    string id, IReadOnlyList<string> attackerRoles, string citizensRole,
    double maxPerSecond, double minCitizenRate) : ICheck
{
    private const int FloodSeconds = 6;
    private const int CitizenRequests = 20;

    public string Id => id;

    public async Task<CheckResult> RunAsync(CheckContext context, CancellationToken ct = default)
    {
        var rates = new List<(string Role, double PerSecond, FloodReport Sent)>();
        var answered = 0;

        foreach (var attacker in attackerRoles)
        {
            var address = context.AddressOf(attacker);
            var flood = Traffic.FloodAsync(context, attacker, FloodSeconds, ct);
            var citizens = Traffic.OrdinaryRequestsAsync(context, citizensRole, CitizenRequests, ct);

            await Task.Delay(1500, ct);
            var clock = Stopwatch.StartNew();
            var before = await context.ServedToAsync(address, ct);
            await Task.Delay(3500, ct);
            var after = await context.ServedToAsync(address, ct);
            var perSecond = (after - before) / clock.Elapsed.TotalSeconds;

            rates.Add((attacker, perSecond, await flood));
            answered += await citizens;
        }

        var sent = CitizenRequests * attackerRoles.Count;
        var citizenRate = (double)answered / sent;
        var worst = rates.Max(r => r.PerSecond);
        var passed = worst <= maxPerSecond && citizenRate >= minCitizenRate;

        var summary = string.Join(", ", rates.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"{r.Role} {r.PerSecond:0.0}/s [sent {r.Sent.Sent}, answered {r.Sent.Answered}{r.Sent.Problem}]")));
        return new CheckResult(id, passed,
            $"got through: {summary} (max {maxPerSecond:0}/s); " +
            $"{citizensRole}: {answered}/{sent} answered (needs {minCitizenRate:P0})");
    }
}

/// <summary>What the flooding host itself saw: how much it sent and how much was answered.</summary>
public sealed record FloodReport(int Sent, int Answered, string Problem);

/// <summary>Scripted traffic, sent with standard tools from the lab's own hosts.</summary>
internal static class Traffic
{
    /// <summary>Five requests a second, each given one second to finish. Returns how many got HTTP 200.</summary>
    public static async Task<int> OrdinaryRequestsAsync(
        CheckContext context, string fromRole, int requests, CancellationToken ct)
    {
        var script =
            $"for i in $(seq {requests}); do " +
            $"curl -s -o /dev/null -m 1 -w '%{{http_code}}\\n' http://{context.WebAddress}/ & sleep 0.2; " +
            "done; wait";
        var result = await context.ShellAsync(fromRole, script, ct);
        return result.Output.Split('\n').Count(line => line.Trim() == "200");
    }

    /// <summary>
    /// A steady stream of requests for the given time, roughly 40 a second.
    /// Evenly spaced on purpose: clumps would leave gaps in which a shared
    /// limit has refilled, and ordinary requests would slip through unharmed.
    /// </summary>
    public static async Task<FloodReport> FloodAsync(
        CheckContext context, string fromRole, int seconds, CancellationToken ct)
    {
        var script =
            $"end=$(( $(date +%s) + {seconds} )); " +
            "while [ $(date +%s) -lt $end ]; do " +
            $"curl -s -o /dev/null -m 1 -w '%{{http_code}}\\n' http://{context.WebAddress}/ & sleep 0.02; " +
            "done; wait";
        var result = await context.ShellAsync(fromRole, script, ct);

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var codes = lines.Where(l => l.Length == 3 && l.All(char.IsDigit)).ToList();
        var other = lines.FirstOrDefault(l => !(l.Length == 3 && l.All(char.IsDigit)));
        var problem = result.ExitCode != 0 || other is not null
            ? $", exit {result.ExitCode}{(other is null ? "" : $": {other}")}"
            : "";
        return new FloodReport(codes.Count, codes.Count(c => c == "200"), problem);
    }
}
