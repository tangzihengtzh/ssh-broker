using System.Text.Json;
using SshBroker.Core;

var items = args.ToList();
string pipe = "SshBrokerV2"; int timeout = 15000;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    while (items.Count > 0 && items[0] is "--pipe" or "--rpc-timeout")
    {
        if (items.Count < 2) throw new BrokerException("invalid_params", "Missing global option value.");
        if (items[0] == "--pipe") pipe = items[1];
        else timeout = int.Parse(items[1], System.Globalization.CultureInfo.InvariantCulture);
        items.RemoveRange(0, 2);
    }
    if (timeout is < 100 or > 360000) throw new BrokerException("invalid_params", "RPC timeout must be 100..360000 ms.");
    if (items.Count == 0 || items[0] is "--help" or "-h")
    {
        Console.WriteLine("sshctl [--pipe NAME] [--rpc-timeout MS] COMMAND [OPTIONS]\n" +
            "Commands: " + string.Join(", ", CommandDispatcher.Commands) + "\n" +
            "Workspace: sessions, session-create --name NAME, session-close --id ID, session-rename --id ID --name NAME, history, palette --value NAME\n" +
            "Use --pipe SESSION_PIPE for configure/connect/send/watch. Default workspace pipe: SshBrokerV2.\n" +
            "sshctl --script commands.sshb | --script - | --repl | --json\n" +
            "sshctl configure --host 192.0.2.1 --user root --password-env SSH_PASSWORD --fingerprint SHA256:...\n" +
            "sshctl send --client agent --text help --cr\n" +
            "sshctl watch [--after 0] [--json-lines]\n" +
            "JSON responses include the same authoritative snapshot used by the GUI. Default write policy: shared.");
        return 0;
    }
    if (items[0] is "--script" or "--repl")
    {
        bool repl = items[0] == "--repl";
        if (repl ? items.Count != 1 : items.Count != 2) throw new BrokerException("invalid_params", "Provide one script path, or --repl.");
        using var file = !repl && items[1] != "-" ? File.OpenText(items[1]) : null;
        TextReader reader = file ?? Console.In; string? line; int lineNumber = 0;
        while ((line = await reader.ReadLineAsync(stop.Token)) != null)
        {
            lineNumber++; var tokens = ScriptTokenizer.Tokenize(line);
            if (tokens.Length == 0) continue;
            var response = await RpcClient.CallAsync(pipe, CommandParser.Parse(tokens), timeout, stop.Token);
            Console.WriteLine(JsonSerializer.Serialize(response, Wire.Json));
            if (!response.Ok && !repl) { Console.Error.WriteLine($"Script stopped at line {lineNumber}."); return ExitCode(response.Code); }
        }
        return 0;
    }
    if (items[0] == "watch")
    {
        var options = items.Skip(1).ToList(); bool json = options.Remove("--json-lines");
        var map = CommandParser.ParseOptions(options);
        if (map.Keys.Any(k => k != "after")) throw new BrokerException("invalid_params", "watch accepts --after and --json-lines.");
        long cursor = long.Parse(map.GetValueOrDefault("after", "0"), System.Globalization.CultureInfo.InvariantCulture);
        var stdout = Console.OpenStandardOutput();
        while (!stop.IsCancellationRequested)
        {
            var response = await RpcClient.CallAsync(pipe, RpcRequest.Create("output", new { after = cursor, limit = 60 }), timeout, stop.Token);
            if (!response.Ok) { Console.Error.WriteLine(JsonSerializer.Serialize(response, Wire.Json)); return ExitCode(response.Code); }
            var page = ((JsonElement)response.Result!).Deserialize<OutputPage>(Wire.Json)!;
            if (page.LostHistory) Console.Error.WriteLine("Output history was truncated before this cursor.");
            foreach (var entry in page.Entries)
            {
                if (json) Console.WriteLine(JsonSerializer.Serialize(entry, Wire.Json));
                else if (entry.Direction == "rx") { await stdout.WriteAsync(Convert.FromBase64String(entry.Base64), stop.Token); await stdout.FlushAsync(stop.Token); }
            }
            cursor = page.Next;
            if (response.Snapshot.State == "faulted") return 4;
            await Task.Delay(100, stop.Token);
        }
        return 0;
    }
    RpcRequest request;
    if (items[0] == "--json")
    {
        if (items.Count != 1) throw new BrokerException("invalid_params", "--json reads one request from stdin.");
        string? line = await Console.In.ReadLineAsync(stop.Token);
        request = JsonSerializer.Deserialize<RpcRequest>(line ?? "", Wire.Json) ?? throw new BrokerException("invalid_request", "Expected a JSON request.");
    }
    else request = CommandParser.Parse(items);
    var result = await RpcClient.CallAsync(pipe, request, timeout, stop.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
    return result.Ok ? 0 : ExitCode(result.Code);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, code = stop.IsCancellationRequested ? "cancelled" : "rpc_timeout",
        message = "Request cancelled or timed out. A write/connect may already have happened; inspect status before retrying." }, Wire.Json));
    return stop.IsCancellationRequested ? 130 : 5;
}
catch (Exception e)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false,
        code = e is BrokerException b ? b.Code : e is JsonException ? "invalid_request" : e is FormatException or OverflowException ? "invalid_params" : "client_error",
        message = e is JsonException ? "Invalid JSON input." : e.Message }, Wire.Json));
    return 2;
}
static int ExitCode(string code) => code is "wait_timeout" or "connect_timeout" ? 5 : code is "invalid_params" or "unknown_command" or "invalid_request" ? 2 : 3;
