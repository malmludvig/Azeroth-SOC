using System.Diagnostics;
using Game.Orchestration;

// Runs a lab without the game client.
//
//   up [--seed N] [--replace]   build a new run
//   status                      show the current run
//   reset                       rebuild the current run from the images, same seed
//   down                        remove the current run
//   sweep [--expired]           remove every lab run, or only those past their lifetime
//   soak [N]                    up, reset, down N times and check that nothing is left behind

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
        default:
            Console.WriteLine("Usage: Game.Cli up [--seed N] [--replace] | status | reset | down | sweep [--expired] | soak [N]");
            return command == "help" ? 0 : 2;
    }
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
    Console.Error.WriteLine($"Cannot reach Docker: {e.Message}");
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

static void Print(LabRun run)
{
    Console.WriteLine($"Run {run.RunId}  scenario {run.Spec.Scenario}  seed {run.Spec.Seed}  networks {run.Driver}");
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
