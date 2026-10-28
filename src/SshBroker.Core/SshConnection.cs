using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace SshBroker.Core;

public sealed partial class BrokerEngine
{
    public async Task ConnectAsync(CancellationToken token = default)
    {
        await StopReconnectAsync().ConfigureAwait(false);
        await ConnectCoreAsync(false, token).ConfigureAwait(false);
    }
    private async Task ConnectCoreAsync(bool automatic, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _faultCleanup.ConfigureAwait(false);
            if (!automatic)
            {
                lock (_gate) if (_state is "connected" or "connecting") throw new BrokerException("already_connected", "An SSH connection already exists.");
                await CleanupTransportAsync().ConfigureAwait(false);
            }
            BrokerConfig config; string password, passphrase; long generation;
            lock (_gate)
            {
                if (_disposed) throw new BrokerException("service_stopped", "Service is shutting down.");
                token.ThrowIfCancellationRequested();
                if (_state is "connected" or "connecting") throw new BrokerException("already_connected", "An SSH connection already exists.");
                config = _config; config.Validate();
                if (string.IsNullOrWhiteSpace(config.Username)) throw new BrokerException("invalid_config", "Username is required.");
                if (config.Authentication == "password" && _password.Length == 0)
                    throw new BrokerException("credentials_missing", "Provide a password through GUI, stdin JSON, or --password-env.");
                if (config.Authentication == "key" && !File.Exists(config.PrivateKeyPath))
                    throw new BrokerException("credentials_missing", "Private key file does not exist.");
                password = _password; passphrase = _keyPassphrase;
                generation = ++_generation; _epoch++; _error = null; _observedKey = "";
                string endpoint = $"{config.Host.ToLowerInvariant()}:{config.Port}/{config.Username}";
                if (endpoint != _historyEndpoint) { _telnet.ClearHistory(); _historyEndpoint = endpoint; }
                _state = automatic ? "reconnecting" : "connecting";
                if (!automatic) { _rx = _tx = 0; _clients.Clear(); _reconnectAttempt = 0; }
                _connectionStop?.Dispose(); _connectionStop = new();
                EventLocked("connecting", $"host={config.Host} port={config.Port} user={config.Username}");
            }
            PrivateKeyFile? key = null;
            try
            {
                _log.Open(config.LogRoot, generation, config.LogLimitBytes);
                lock (_gate) EventLocked("session_start", "New SSH session; no commands are replayed.");
                AuthenticationMethod authentication;
                if (config.Authentication == "key")
                {
                    key = passphrase.Length == 0 ? new(config.PrivateKeyPath) : new(config.PrivateKeyPath, passphrase);
                    authentication = new PrivateKeyAuthenticationMethod(config.Username, new ModernKeySource(key));
                }
                else authentication = new PasswordAuthenticationMethod(config.Username, password);
                var info = new ConnectionInfo(config.Host, config.Port, config.Username, authentication)
                { Timeout = TimeSpan.FromMilliseconds(config.ConnectTimeoutMs) };
                var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(config.KeepAliveSeconds) };
                lock (_gate) _client = client;
                client.HostKeyReceived += (_, args) =>
                {
                    string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
                    lock (_gate)
                    {
                        if (generation != _generation) { args.CanTrust = false; return; }
                        _observedKey = fingerprint; _revision++;
                        args.CanTrust = string.Equals(config.HostKeyFingerprint, fingerprint, StringComparison.Ordinal);
                    }
                };
                client.ErrorOccurred += (_, args) => Fault(generation, "transport_lost", args.Exception.GetType().Name + ": " + args.Exception.Message);
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token, _connectionStop!.Token);
                connect.CancelAfter(config.ConnectTimeoutMs);
                await client.ConnectAsync(connect.Token).ConfigureAwait(false);
                var open = Task.Run(() => client.CreateShellStream("xterm-256color", (uint)config.Columns, (uint)config.Rows, 0, 0, 32768));
                ShellStream shell;
                try { shell = await open.WaitAsync(TimeSpan.FromMilliseconds(config.ConnectTimeoutMs), connect.Token).ConfigureAwait(false); }
                catch { _ = open.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }); throw; }
                shell.ErrorOccurred += (_, args) => Fault(generation, "shell_error", args.Exception.GetType().Name + ": " + args.Exception.Message);
                lock (_gate)
                {
                    _shell = shell;
                }
                // A listening Telnet port must mean the upstream PTY is ready.
                if (config.TelnetEnabled && !_telnet.Listening) _telnet.Start(config.TelnetPort, preserveHistory: true);
                lock (_gate)
                {
                    _state = "connected"; _revision++;
                    _connectedAt = DateTimeOffset.UtcNow;
                    if (automatic) _reconnectSuccesses++;
                    EventLocked("connected", $"SSH PTY established; fingerprint={_observedKey}");
                }
                if (automatic) Announce("SSH reconnected. New remote shell; old commands were not replayed.");
                var stop = _connectionStop.Token;
                _reader = Task.Run(() => ReadLoop(shell, generation, stop));
                _health = HealthLoopAsync(client, generation, config, stop);
            }
            catch (Exception exception)
            {
                string code, message;
                lock (_gate)
                {
                    code = _observedKey.Length > 0 && config.HostKeyFingerprint != _observedKey
                        ? (config.HostKeyFingerprint.Length == 0 ? "host_key_unknown" : "host_key_mismatch")
                        : exception is SshAuthenticationException ? "authentication_failed"
                        : exception is OperationCanceledException && (_connectionStop!.IsCancellationRequested || token.IsCancellationRequested) ? "cancelled"
                        : exception is OperationCanceledException or TimeoutException or SshOperationTimeoutException ? "connect_timeout"
                        : "connect_failed";
                    message = code is "host_key_unknown" or "host_key_mismatch"
                        ? "Host key was rejected. Verify the observed fingerprint out of band before configuring it."
                        : Sanitize(exception.Message);
                    _state = automatic && !token.IsCancellationRequested ? "reconnecting" : "faulted";
                    _error = new(code, message); _epoch++; _revision++;
                    EventLocked(code, message);
                }
                await CleanupTransportAsync(preserveTelnet: automatic).ConfigureAwait(false);
                throw new BrokerException(code, message);
            }
            finally { key?.Dispose(); }
        }
        finally { _lifecycle.Release(); }
    }

    private void ReadLoop(ShellStream shell, long generation, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count = shell.Read(buffer, 0, buffer.Length);
                if (count == 0) { if (!token.IsCancellationRequested) Fault(generation, "shell_closed", "Remote SSH shell closed."); break; }
                Received(generation, buffer.AsSpan(0, count).ToArray());
            }
        }
        catch (Exception e) { if (!token.IsCancellationRequested) Fault(generation, "read_failed", e.GetType().Name + ": " + e.Message); }
    }
    private async Task HealthLoopAsync(SshClient client, long generation, BrokerConfig config, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(config.KeepAliveSeconds), token).ConfigureAwait(false);
                if (!client.IsConnected) { Fault(generation, "transport_lost", "SSH transport disconnected."); return; }
                if (config.HealthCommand.Length == 0) continue;
                using var probe = client.CreateCommand(config.HealthCommand);
                probe.CommandTimeout = TimeSpan.FromMilliseconds(config.HealthTimeoutMs);
                await probe.ExecuteAsync(token).WaitAsync(TimeSpan.FromMilliseconds(config.HealthTimeoutMs), token).ConfigureAwait(false);
                if (probe.ExitStatus != 0) { Fault(generation, "health_failed", "Health command returned a nonzero status; configure healthCommand if required."); return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { if (!token.IsCancellationRequested) Fault(generation, "health_timeout", e.GetType().Name + ": " + e.Message); }
    }
    private void Fault(long generation, string code, string message)
    {
        lock (_gate)
        {
            if (generation != _generation || _state != "connected") return;
            var now = DateTimeOffset.UtcNow;
            if (now - _connectedAt >= TimeSpan.FromSeconds(30)) _reconnectAttempt = 0;
            _lastDisconnect = new(code, Sanitize(message)); _lastDisconnectAt = now;
            bool retry = !_disposed && Retryable(code) && _reconnectAttempt < _config.ReconnectAttempts;
            _state = retry ? "reconnecting" : "faulted"; _error = _lastDisconnect; _epoch++; _revision++;
            _connectionStop?.Cancel();
            EventLocked(code, message);
            EventLocked("disconnect_diagnostic", $"generation={generation}; connected_ms={(long)(now - _connectedAt).TotalMilliseconds}; rx={_rx}; tx={_tx}; cause={code}");
            _faultCleanup = Task.Run(() => CleanupTransportAsync(preserveTelnet: retry));
            if (retry)
            {
                var stop = new CancellationTokenSource(); _reconnectStop = stop; _recovering = true;
                var cleanup = _faultCleanup;
                _reconnectTask = Task.Run(() => ReconnectLoopAsync(cleanup, stop));
            }
            else EventLocked("reconnect_stopped", "Automatic recovery disabled, exhausted, or cause requires user action.");
        }
    }
    private async Task CleanupTransportAsync(bool preserveTelnet = false)
    {
        _connectionStop?.Cancel();
        SshClient? client; ShellStream? shell;
        lock (_gate) { client = _client; shell = _shell; _client = null; _shell = null; }
        // Dispose the transport before waiting for readers so blocked reads/writes can finish.
        await Task.Run(() => { try { client?.Dispose(); } finally { shell?.Dispose(); } }).ConfigureAwait(false);
        if (!preserveTelnet) await _telnet.StopAsync(preserveHistory: true).ConfigureAwait(false);
        try { await Task.WhenAll(_reader, _health).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        lock (_gate)
        {
            if (!preserveTelnet) { _clients.Clear(); _writer = ""; }
            _revision++;
            EventLocked("transport_cleaned", preserveTelnet ? "SSH closed; Telnet sockets/history retained. Offline input is rejected; nothing is replayed." : "SSH and Telnet sessions closed; output history retained; pending writes will not be replayed.");
        }
    }
    public async Task DisconnectAsync(CancellationToken token = default)
    {
        await StopReconnectAsync().ConfigureAwait(false);
        lock (_gate) _connectionStop?.Cancel(); // Also cancels an in-progress connection attempt.
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _faultCleanup.ConfigureAwait(false);
            lock (_gate)
            {
                _state = "disconnecting"; _epoch++; _revision++;
                EventLocked("disconnecting", "Disconnect requested.");
            }
            await CleanupTransportAsync().ConfigureAwait(false);
            lock (_gate) { _state = "disconnected"; _error = null; _revision++; EventLocked("disconnected", "Disconnected by request."); }
        }
        finally { _lifecycle.Release(); }
    }
    public async Task ResizeAsync(int columns, int rows, CancellationToken token = default)
    {
        if (columns is < 20 or > 500 || rows is < 5 or > 200) throw new BrokerException("invalid_params", "PTY dimensions out of range.");
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ShellStream? shell; int timeout; long generation;
            lock (_gate) { shell = _state == "connected" ? _shell : null; timeout = _config.OperationTimeoutMs; generation = _generation; }
            if (shell != null)
            {
                try { await Task.Run(() => shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0)).WaitAsync(TimeSpan.FromMilliseconds(timeout), token).ConfigureAwait(false); }
                catch (Exception e) { Fault(generation, "resize_failed", e.Message); throw new BrokerException("resize_failed", "PTY resize failed."); }
            }
            lock (_gate)
            {
                _config = _config with { Columns = columns, Rows = rows }; _configRevision++; _revision++;
                EventLocked("resize", $"columns={columns} rows={rows}");
            }
        }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        await DisconnectAsync().ConfigureAwait(false);
        lock (_gate) { _password = _keyPassphrase = ""; _log.Dispose(); }
        _connectionStop?.Dispose();
    }

    // SSH.NET orders legacy ssh-rsa first. Servers may disconnect immediately instead of
    // allowing a retry. Prefer modern signatures and never offer RSA/SHA-1 or DSA here.
    private sealed class ModernKeySource(IPrivateKeySource source) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = source.HostKeyAlgorithms
            .Where(a => a.Name is not ("ssh-rsa" or "ssh-dss" or "ssh-rsa-cert-v01@openssh.com" or "ssh-dss-cert-v01@openssh.com"))
            .OrderBy(a => a.Name.Contains("sha2-512", StringComparison.Ordinal) ? 0 : a.Name.Contains("sha2-256", StringComparison.Ordinal) ? 1 : 2).ToArray();
    }
}
