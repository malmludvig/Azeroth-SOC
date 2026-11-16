using System.Text.RegularExpressions;
using Game.Orchestration;

namespace Game.Core;

public sealed record QuestRequest(string Id, string Verb, string[] Arguments);

/// <summary>
/// The game's side of the quest command. The gate cannot reach the game over
/// the network, so requests and replies are files in /run/quest that the game
/// reads and writes from outside the container.
///
/// Nothing the game decides rests on these files alone: the player is root in
/// the gate and could write them by hand. A report is compared with the truth,
/// and a wave is judged by traffic.
/// </summary>
public sealed partial class GateMailbox(DockerEngine docker, LabRun run)
{
    private const string Dir = "/run/quest";
    private const string Marker = "AZEROTH_END_OF_TEXT";

    public async Task OpenAsync(CancellationToken ct = default) =>
        await ShAsync($"mkdir -p {Dir} && rm -f {Dir}/request {Dir}/request.tmp {Dir}/reply.*", ct);

    /// <summary>Removes the briefing, so that quest says no game is running.</summary>
    public async Task CloseAsync(CancellationToken ct = default) =>
        await ShAsync($"rm -f {Dir}/briefing {Dir}/mode {Dir}/request {Dir}/request.tmp", ct);

    /// <summary>Publishes the current mission and how it is settled (a wave kind, or "over").</summary>
    public async Task PublishAsync(string briefing, string settledBy, CancellationToken ct = default)
    {
        await WriteAsync("mode", settledBy, ct);
        await WriteAsync("briefing", briefing, ct);
    }

    public async Task<QuestRequest?> PollAsync(CancellationToken ct = default)
    {
        var result = await ShAsync($"if [ -f {Dir}/request ]; then cat {Dir}/request; rm -f {Dir}/request; fi", ct);
        var parts = result.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !RequestId().IsMatch(parts[0])) return null;
        return new QuestRequest(parts[0], parts[1], parts[2..]);
    }

    public Task ReplyAsync(QuestRequest request, string text, CancellationToken ct = default) =>
        WriteAsync("reply." + request.Id, text, ct);

    private async Task WriteAsync(string file, string text, CancellationToken ct)
    {
        var safe = text.Replace(Marker, "");
        await ShAsync(
            $"cat > {Dir}/{file}.part <<'{Marker}'\n{safe}\n{Marker}\nmv {Dir}/{file}.part {Dir}/{file}", ct);
    }

    private async Task<ExecResult> ShAsync(string script, CancellationToken ct)
    {
        var result = await docker.ExecAsync(run.ContainerName("gate"), ["sh", "-c", script], ct: ct);
        if (!result.Succeeded) throw new LabException($"The gate's quest files could not be used: {result.Output.Trim()}");
        return result;
    }

    [GeneratedRegex(@"^\d+-\d+$")]
    private static partial Regex RequestId();
}
