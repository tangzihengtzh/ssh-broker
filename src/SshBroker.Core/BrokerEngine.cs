using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Renci.SshNet;

namespace SshBroker.Core;

public sealed partial class BrokerEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _send = new(1, 1);
    private readonly OutputBuffer _output = new();
    private readonly SessionLog _log = new();
    private readonly TelnetFrontEnd _telnet;
    private readonly Dictionary<string, (string Kind, long Tx)> _clients = new();
    private BrokerConfig _config = new();
    private string _state = "disconnected", _policy = "shared", _writer = "", _password = "", _keyPassphrase = "";
    private string _observedKey = "", _reportedLogFailure = "";
    private ErrorInfo? _error;
    private long _revision, _configRevision, _generation, _epoch, _rx, _tx;
    private int _pending;
    private bool _disposed;
    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _connectionStop;
    private Task _reader = Task.CompletedTask, _health = Task.CompletedTask, _faultCleanup = Task.CompletedTask;

    public BrokerEngine()
    {
        _telnet = new(RegisterClient, (id, reason) =>
            { lock (_gate) EventLocked("telnet_closed", $"client={id} reason={reason}"); CloseClient(id); },
            async (id, data) => { await SendAsync(id, data).ConfigureAwait(false); },
            message => Fault(CurrentGeneration, "telnet_failed", message));
    }
    private long CurrentGeneration { get { lock (_gate) return _generation; } }

    public BrokerSnapshot Snapshot()
    {
        lock (_gate) return new()
        {
            Revision = _revision, ConfigRevision = _configRevision, Generation = _generation,
            State = _state, Policy = _policy, Writer = _writer, Config = _config,
            HasPassword = _password.Length > 0, HasKeyPassphrase = _keyPassphrase.Length > 0,
            ObservedHostKey = _observedKey, LastError = _error, RxBytes = _rx, TxBytes = _tx,
            LastDisconnect = _lastDisconnect, LastDisconnectAt = _lastDisconnectAt,
            AutoReconnectActive = _recovering, ReconnectAttempt = _reconnectAttempt, ReconnectSuccesses = _reconnectSuccesses,
            OutputCursor = _output.Cursor, TelnetListening = _telnet.Listening, LocalTelnetPort = _telnet.Port,
            LogDirectory = _log.DirectoryPath, Logging = _log.Active, LogFailure = _log.Failure,
            Clients = _clients.Select(c => new ClientSnapshot(c.Key, c.Value.Kind,
                _state == "connected" && Allowed(c.Key), c.Value.Tx)).OrderBy(c => c.Id).ToArray()
        };
    }

    public async Task ConfigureAsync(JsonElement parameters, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _faultCleanup.ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) throw new BrokerException("service_stopped", "Service is shutting down.");
                if (_state is "connected" or "connecting" or "disconnecting" or "reconnecting" || _recovering)
                    throw new BrokerException("busy", "Disconnect before changing connection configuration. Use resize for a live PTY.");
                if (parameters.TryGetProperty("expectedRevision", out var expected) && expected.GetInt64() != _revision)
                    throw new BrokerException("revision_conflict", "Configuration changed; query status before retrying.");
                var merged = JsonSerializer.SerializeToNode(_config, Wire.Json)!.AsObject();
                foreach (var property in parameters.EnumerateObject())
                {
                    if (property.Name is "password" or "keyPassphrase" or "expectedRevision") continue;
                    if (!merged.ContainsKey(property.Name)) throw new BrokerException("invalid_params", "Unknown configuration field: " + property.Name);
                    merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
                var candidate = merged.Deserialize<BrokerConfig>(Wire.Json)!;
                candidate.Validate();
                var password = Wire.String(parameters, "password", _password);
                var passphrase = Wire.String(parameters, "keyPassphrase", _keyPassphrase);
                if (password.Length > 4096 || passphrase.Length > 4096)
                    throw new BrokerException("invalid_params", "Credential exceeds maximum length.");
                _config = candidate; _password = password; _keyPassphrase = passphrase;
                _configRevision++; _revision++;
                EventLocked("configured", "Connection configuration updated; credentials omitted.");
            }
        }
        finally { _lifecycle.Release(); }
    }

    public void SetPolicy(string policy)
    {
        if (policy is not ("shared" or "single" or "readonly"))
            throw new BrokerException("invalid_params", "Policy must be shared, single, or readonly.");
        lock (_gate)
        {
            if (_policy == policy) return;
            _policy = policy; _writer = ""; _epoch++; _revision++;
            EventLocked("policy", $"policy={policy}; queued requests under the old policy are invalidated.");
        }
    }
    private bool Allowed(string id) => _policy == "shared" || (_policy == "single" && _writer == id);
    private static void ValidateClient(string id)
    {
        if (id.Length is < 1 or > 64 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new BrokerException("invalid_params", "Client ID must be 1..64 ASCII letters, digits, dash, underscore or dot.");
    }
    private void RegisterClient(string id, string kind)
    {
        lock (_gate)
        {
            if (_clients.ContainsKey(id)) return;
            if (_clients.Count >= 64)
            { EventLocked("client_limit", "Logical client limit reached; new client rejected."); throw new BrokerException("client_limit", "At most 64 logical clients are supported."); }
            _clients.Add(id, (kind, 0)); _revision++;
            EventLocked("client_open", $"client={id} kind={kind}");
        }
    }
    public void CloseClient(string id)
    {
        lock (_gate)
        {
            if (!_clients.Remove(id)) return;
            if (_writer == id) { _writer = ""; _epoch++; }
            _revision++; EventLocked("client_close", $"client={id}");
        }
        _telnet.Disconnect(id);
    }
    public void Acquire(string id)
    {
        ValidateClient(id);
        lock (_gate)
        {
            if (_state != "connected") throw new BrokerException("not_connected", "No active SSH PTY.");
            if (_policy == "readonly" || (_policy == "single" && _writer.Length > 0 && _writer != id))
                throw new BrokerException("write_denied", "Writer lease unavailable under the current policy.");
            RegisterClient(id, "cli");
            if (_policy == "single" && _writer != id) { _writer = id; _epoch++; _revision++; }
            EventLocked("acquire", $"client={id}");
        }
    }
    public void Release(string id)
    {
        lock (_gate)
        {
            if (_writer != id && _policy != "shared") throw new BrokerException("write_denied", "Client does not own the writer lease.");
            if (_writer == id) { _writer = ""; _epoch++; _revision++; }
            EventLocked("release", $"client={id}");
        }
    }

    public async Task<int> SendAsync(string id, byte[] data, CancellationToken token = default)
    {
        ValidateClient(id);
        if (data.Length is < 1 or > 4096) throw new BrokerException("invalid_params", "Send accepts 1..4096 bytes.");
        if (Interlocked.Increment(ref _pending) > 128)
        { Interlocked.Decrement(ref _pending); throw new BrokerException("queue_full", "Too many queued writes."); }
        long epoch, generation;
        bool ownsLock = false;
        try
        {
            lock (_gate)
            {
                if (_state != "connected")
                {
                    EventLocked("offline_input_dropped", $"client={id}; bytes={data.Length}; not queued or replayed");
                    throw new BrokerException("not_connected", "No active SSH PTY; input was not queued.");
                }
                if (!Allowed(id) && !(_policy == "single" && _writer.Length == 0))
                { EventLocked("write_denied", $"client={id}"); throw new BrokerException("write_denied", "Current policy denies this client's writes."); }
                RegisterClient(id, id.StartsWith("telnet-", StringComparison.Ordinal) ? "telnet" : "cli");
                if (_policy == "single" && _writer.Length == 0)
                { _writer = id; _epoch++; _revision++; EventLocked("acquire", $"client={id}; first sender"); }
                epoch = _epoch; generation = _generation;
            }
            await _send.WaitAsync(token).ConfigureAwait(false); ownsLock = true;
            ShellStream shell; int timeout;
            lock (_gate)
            {
                if (_state != "connected" || generation != _generation)
                    throw new BrokerException("not_connected", "Connection ended while write was queued; nothing was retried.");
                if (epoch != _epoch || !Allowed(id) || !_clients.ContainsKey(id))
                    throw new BrokerException("write_revoked", "Write policy or lease changed while request was queued.");
                shell = _shell!; timeout = _config.OperationTimeoutMs;
            }
            // Once admitted, a block is in flight. Cancelling an RPC must not silently retry it.
            try
            {
                var write = Task.Run(() => { shell.Write(data, 0, data.Length); shell.Flush(); });
                try { await write.WaitAsync(TimeSpan.FromMilliseconds(timeout)).ConfigureAwait(false); }
                catch { _ = write.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
            }
            catch (Exception e)
            {
                Fault(generation, "write_failed", "SSH write failed; partial transmission is possible. " + e.Message);
                throw new BrokerException("write_failed", "SSH write failed or timed out; partial transmission is possible. Do not retry automatically.");
            }
            lock (_gate)
            {
                if (generation != _generation || _state != "connected")
                    throw new BrokerException("write_uncertain", "Connection ended during write; transmission may already have completed.");
                _tx += data.Length;
                if (_clients.TryGetValue(id, out var client)) _clients[id] = (client.Kind, client.Tx + data.Length);
                _revision++; _log.Data(true, data); _output.Add("tx", id, data);
                EventLocked("tx", $"client={id} bytes={data.Length}; accepted by SSH transport, not a remote command exit status.");
            }
            return data.Length;
        }
        finally { if (ownsLock) _send.Release(); Interlocked.Decrement(ref _pending); }
    }

    private void Received(long generation, byte[] data)
    {
        lock (_gate)
        {
            if (generation != _generation || _state != "connected") return;
            _rx += data.Length; _revision++; _log.Data(false, data); _output.Add("rx", "remote", data);
            CheckLogFailureLocked();
        }
        _telnet.Broadcast(data);
    }
    public OutputPage Output(long after, int limit = 100) => _output.Read(after, limit);
    public void Audit(string method, string code)
    {
        if (method is "status" or "output" or "projection" or "help" or "clients" or "logs") return;
        lock (_gate) EventLocked("command", $"method={method} code={code}; arguments omitted.");
    }
    private string Sanitize(string text)
    {
        static string Mask(string value, string secret) => secret.Length == 0 ? value : secret.Length > 3
            ? value.Replace(secret, "[REDACTED]", StringComparison.Ordinal)
            : System.Text.RegularExpressions.Regex.Replace(value, @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(secret) + @"(?![\p{L}\p{N}])", "[REDACTED]");
        text = Mask(Mask(text, _password), _keyPassphrase);
        return text.Length > 2000 ? text[..2000] : text;
    }
    private void EventLocked(string kind, string message)
    {
        message = Sanitize(message); _revision++;
        _output.Add("event", kind, [], message);
        _log.Event(kind, message, _revision, _generation);
        CheckLogFailureLocked();
    }
    private void CheckLogFailureLocked()
    {
        if (_log.Failure.Length == 0 || _reportedLogFailure == _log.Failure) return;
        _reportedLogFailure = _log.Failure; _revision++;
        _output.Add("event", "logging_disabled", [], "LOGGING DISABLED: " + Sanitize(_log.Failure));
    }
}
