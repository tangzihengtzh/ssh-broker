using System.Text.Json;
using SshBroker.Core;

try
{
    var options = CommandParser.ParseOptions(args);
    if (options.GetValueOrDefault("workspace", "false") == "true")
    {
        var workspacePipe = options.GetValueOrDefault("pipe", "SshBrokerV2");
        var directory = options.GetValueOrDefault("data-directory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshBroker", "workspace"));
        await using var workspace = new WorkspaceRuntime(workspacePipe, directory);
        workspace.Start();
        using var workspaceStop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; workspaceStop.Cancel(); };
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pipe = workspacePipe, version = "2.0.0", kind = "workspace" }, Wire.Json));
        try { await workspace.Shutdown.WaitAsync(workspaceStop.Token); } catch (OperationCanceledException) { }
        return 0;
    }
    if (options.Keys.Any(k => k is not ("pipe" or "log-root"))) throw new BrokerException("invalid_params", "Host accepts --pipe, --log-root, or --workspace true with --data-directory.");
    var pipe = options.GetValueOrDefault("pipe", "SshBroker");
    await using var runtime = new BrokerRuntime(pipe);
    if (options.TryGetValue("log-root", out var logs)) await runtime.Engine.ConfigureAsync(Wire.Element(new { logRoot = logs }));
    runtime.Start();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pipe, version = "2.0.0" }, Wire.Json));
    try { await runtime.Shutdown.WaitAsync(stop.Token); } catch (OperationCanceledException) { }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, code = e is BrokerException b ? b.Code : "host_error", message = e.Message }, Wire.Json));
    return 2;
}
