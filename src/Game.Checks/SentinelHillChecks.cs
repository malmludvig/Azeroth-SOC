namespace Game.Checks;

public sealed record WaveVerdict(int Wave, IReadOnlyList<CheckResult> Results)
{
    public bool Passed => Results.All(r => r.Passed);
}

/// <summary>
/// The judged conditions of Sentinel Hill, wave by wave. Each wave keeps the
/// conditions of the one before: a defence must not open up what it closed.
/// Wave 0 (map the gate) is a report from the player and has no traffic check.
/// </summary>
public static class SentinelHillChecks
{
    public const int FirstWave = 1;
    public const int LastWave = 3;

    public static IReadOnlyList<ICheck> ForWave(int wave)
    {
        if (wave < FirstWave || wave > LastWave)
            throw new ArgumentOutOfRangeException(nameof(wave), $"Waves with checks are {FirstWave} to {LastWave}.");

        // Wave 1, the scouts: only the notice board is public.
        var checks = new List<ICheck>
        {
            new PortsCheck("scouts-reach-only-the-board", "defias", ["http"], ["management", "debug", "database"]),
            new SuccessRateCheck("citizens-are-served", "citizens", requests: 20, minimum: 0.99),
        };

        // Wave 2, the break-in: management from the garrison and nobody else.
        if (wave >= 2)
        {
            checks.Add(new PortsCheck("garrison-can-manage", "garrison", ["management", "http"], []));
            checks.Add(new PortsCheck("probe-cannot-manage", "probe", ["http"], ["management"]));
            checks.Add(new PortsCheck("citizens-cannot-manage", "citizens", ["http"], ["management"]));
        }

        // Wave 3, the swarm: no source gets more than 20 requests a second through.
        if (wave >= 3)
        {
            checks.Add(new FloodCheck("swarm-is-held-back", ["defias", "probe"], "citizens",
                maxPerSecond: 20, minCitizenRate: 0.95));
        }

        return checks;
    }

    public static async Task<WaveVerdict> JudgeAsync(int wave, CheckContext context, CancellationToken ct = default)
    {
        var results = new List<CheckResult>();
        foreach (var check in ForWave(wave))
            results.Add(await check.RunAsync(context, ct));
        return new WaveVerdict(wave, results);
    }
}
