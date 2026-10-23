using System.Text;
using System.Text.Json;

namespace SshBroker.Core;

public sealed class CommandDispatcher(BrokerEngine engine)
{
    public event Action? ShutdownRequested;
    public static readonly string[] Commands = ["help", "status", "configure", "connect", "disconnect", "reconnect",
        "policy", "send", "acquire", "release", "client-close", "clients", "resize", "output", "logs", "projection", "wait", "wait-output", "shutdown"];
    public async Task<RpcResponse> ExecuteAsync(RpcRequest request, CancellationToken token = default)
    {
        try
        {
            if (request.Id.Length > 128 || request.Params.ValueKind != JsonValueKind.Object)
                throw new BrokerException("invalid_request", "id must be a short string and params must be an object.");
            object? result = null; var p = request.Params;
            switch (request.Method)
            {
                case "help": result = new { protocol = 1, commands = Commands, defaultPolicy = "shared", script = "quoted commands, not a Tcl interpreter" }; break;
                case "status": break; // Filled from the same atomic snapshot as the response envelope.
                case "configure": await engine.ConfigureAsync(p, token).ConfigureAwait(false); break;
                case "connect": await engine.ConnectAsync(token).ConfigureAwait(false); break;
                case "disconnect": await engine.DisconnectAsync(token).ConfigureAwait(false); break;
                case "reconnect":
                    await engine.DisconnectAsync(token).ConfigureAwait(false);
                    await engine.ConnectAsync(token).ConfigureAwait(false); break;
                case "policy": engine.SetPolicy(Wire.String(p, "value")); break;
                case "send":
                    byte[] bytes;
                    try
                    {
                        bytes = p.TryGetProperty("base64", out var encoded) ? Convert.FromBase64String(encoded.GetString() ?? "")
                            : Encoding.UTF8.GetBytes(Wire.String(p, "text"));
                    }
                    catch (FormatException) { throw new BrokerException("invalid_params", "Invalid base64 payload."); }
                    var sent = await engine.SendAsync(Wire.String(p, "client", "agent"), bytes, token).ConfigureAwait(false);
                    result = new { bytes = sent, acknowledgement = "ssh_transport_write", commandCompleted = false }; break;
                case "acquire": engine.Acquire(Wire.String(p, "client", "agent")); break;
                case "release": engine.Release(Wire.String(p, "client", "agent")); break;
                case "client-close": engine.CloseClient(Wire.String(p, "client", "agent")); break;
                case "clients": break;
                case "resize": await engine.ResizeAsync(Wire.Int(p, "columns", 120), Wire.Int(p, "rows", 32), token).ConfigureAwait(false); break;
                case "output": result = engine.Output(p.TryGetProperty("after", out var a) ? a.GetInt64() : 0, Wire.Int(p, "limit", 100)); break;
                case "logs": break;
                case "projection": break;
                case "wait": result = await WaitAsync(p, token).ConfigureAwait(false); break;
                case "wait-output": result = await WaitOutputAsync(p, token).ConfigureAwait(false); break;
                case "shutdown": _ = Task.Run(async () => { await Task.Delay(150).ConfigureAwait(false); ShutdownRequested?.Invoke(); }); break;
                default: throw new BrokerException("unknown_command", "Unknown method: " + request.Method);
            }
            engine.Audit(request.Method, "ok");
            var snapshot = engine.Snapshot();
            if (request.Method == "status") result = snapshot;
            else if (request.Method == "clients") result = snapshot.Clients;
            else if (request.Method == "logs") result = new { directory = snapshot.LogDirectory, active = snapshot.Logging, failure = snapshot.LogFailure };
            else if (request.Method == "projection")
            {
                var view = new SnapshotProjection(); view.Apply(snapshot);
                result = new { view.Revision, view.ConfigRevision, view.Generation, view.State, view.Host, view.Port,
                    view.Username, view.Policy, view.Writer, view.RxBytes, view.TxBytes, view.Error, view.Fingerprint, view.Logging, view.LocalTelnetPort, config = snapshot.Config };
            }
            return new(request.Id, true, "ok", "", result, snapshot);
        }
        catch (Exception e)
        {
            var code = e is BrokerException b ? b.Code : e is OperationCanceledException ? "cancelled" :
                e is JsonException or InvalidOperationException or FormatException or OverflowException ? "invalid_params" : "internal_error";
            // Library exceptions are mapped inside the engine. Do not return raw credential-bearing JSON here.
            var message = e is BrokerException ? e.Message : code == "internal_error" ? "Internal operation failed; inspect event logs." : "Invalid arguments or operation cancelled.";
            engine.Audit(request.Method, code);
            return new(request.Id, false, code, message, null, engine.Snapshot());
        }
    }
    private async Task<object> WaitAsync(JsonElement p, CancellationToken token)
    {
        var state = Wire.String(p, "state"); int ms = Wire.Int(p, "ms", 5000);
        if (ms is < 1 or > 300000 || state is not ("connected" or "disconnected" or "connecting" or "disconnecting" or "faulted"))
            throw new BrokerException("invalid_params", "Invalid wait state or deadline.");
        var deadline = Environment.TickCount64 + ms;
        do
        {
            var current = engine.Snapshot();
            if (current.State == state) return new { reached = state, current.Revision };
            if (current.State == "faulted") throw new BrokerException("connection_lost", "Connection faulted while waiting: " + current.LastError?.Code);
            await Task.Delay(50, token).ConfigureAwait(false);
        } while (Environment.TickCount64 < deadline);
        throw new BrokerException("wait_timeout", "Timed out waiting for state " + state);
    }
    private async Task<object> WaitOutputAsync(JsonElement p, CancellationToken token)
    {
        string contains = Wire.String(p, "contains"); int ms = Wire.Int(p, "ms", 5000);
        long cursor = p.TryGetProperty("after", out var a) ? a.GetInt64() : engine.Snapshot().OutputCursor;
        if (contains.Length is < 1 or > 4096 || ms is < 1 or > 300000) throw new BrokerException("invalid_params", "Invalid output match or deadline.");
        string pending = ""; var deadline = Environment.TickCount64 + ms;
        do
        {
            var page = engine.Output(cursor);
            if (page.LostHistory) throw new BrokerException("history_lost", "Output cursor fell behind the bounded history.");
            foreach (var entry in page.Entries)
            {
                cursor = entry.Sequence;
                if (entry.Direction != "rx") continue;
                pending += entry.Text;
                if (pending.Contains(contains, StringComparison.Ordinal)) return new { matched = contains, cursor };
                if (pending.Length > 65536) pending = pending[^8192..];
            }
            var state = engine.Snapshot().State;
            if (state is "faulted" or "disconnected") throw new BrokerException("connection_lost", "SSH disconnected while waiting for output.");
            await Task.Delay(50, token).ConfigureAwait(false);
        } while (Environment.TickCount64 < deadline);
        throw new BrokerException("wait_timeout", "Timed out waiting for output.");
    }
}
