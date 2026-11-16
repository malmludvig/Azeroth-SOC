using Game.Orchestration;

namespace Game.Checks;

public sealed record CheckResult(string Id, bool Passed, string Detail);

/// <summary>
/// One judged condition of a wave. Checks judge behaviour: they send real
/// traffic and look at what arrives. They never look at how a rule is written.
/// </summary>
public interface ICheck
{
    string Id { get; }

    Task<CheckResult> RunAsync(CheckContext context, CancellationToken ct = default);
}

/// <summary>
/// What a check may touch. Everything it measures comes from containers the
/// player has no shell in: the hosts on the plains and the web server.
/// </summary>
public sealed class CheckContext(DockerEngine docker, LabRun run)
{
    public const string AccessLog = "/var/log/nginx/board.access.log";

    public LabSpec Spec => run.Spec;

    public string WebAddress => Spec.Node("web").AddressOn(SentinelHill.Dmz);

    public int Port(string name) => int.Parse(Spec.Parameters["web." + name]);

    public string AddressOf(string role) => Spec.Node(role).AddressOn(SentinelHill.Plains);

    public Task<ExecResult> ExecAsync(string role, IReadOnlyList<string> command, CancellationToken ct = default) =>
        docker.ExecAsync(run.ContainerName(role), command, ct: ct);

    public Task<ExecResult> ShellAsync(string role, string script, CancellationToken ct = default) =>
        ExecAsync(role, ["sh", "-c", script], ct);

    public async Task<bool> CanConnectAsync(string fromRole, int port, CancellationToken ct = default) =>
        (await ExecAsync(fromRole, ["nc", "-z", "-w", "2", WebAddress, port.ToString()], ct)).Succeeded;

    /// <summary>Requests the web server has served to this source address so far.</summary>
    public async Task<int> ServedToAsync(string sourceAddress, CancellationToken ct = default)
    {
        var result = await ShellAsync("web", $"grep -c '^{sourceAddress} ' {AccessLog} || true", ct);
        return int.TryParse(result.Output.Trim(), out var count) ? count : 0;
    }
}
