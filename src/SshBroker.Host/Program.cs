using System.Text.Json;
using SshBroker.Core;

try
{
    var options = CommandParser.ParseOptions(args);
    if (options.Keys.Any(k => k is not ("pipe" or "log-root"))) throw new BrokerException("invalid_params", "Host accepts --pipe and --log-root.");
    var pipe = options.GetValueOrDefault("pipe", "SshBroker");
    await using var runtime = new BrokerRuntime(pipe);
    if (options.TryGetValue("log-root", out var logs)) await runtime.Engine.ConfigureAsync(Wire.Element(new { logRoot = logs }));
    runtime.Start();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pipe, version = "0.1.0" }, Wire.Json));
    try { await runtime.Shutdown.WaitAsync(stop.Token); } catch (OperationCanceledException) { }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, code = e is BrokerException b ? b.Code : "host_error", message = e.Message }, Wire.Json));
    return 2;
}
