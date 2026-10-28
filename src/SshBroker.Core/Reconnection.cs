using System.Text;

namespace SshBroker.Core;

public sealed partial class BrokerEngine
{
    private CancellationTokenSource? _reconnectStop;
    private Task _reconnectTask = Task.CompletedTask;
    private bool _recovering;
    private int _reconnectAttempt;
    private long _reconnectSuccesses;
    private ErrorInfo? _lastDisconnect;
    private DateTimeOffset? _lastDisconnectAt;
    private DateTimeOffset _connectedAt;
    private string _historyEndpoint = "";
    private static bool Retryable(string code) => code is "transport_lost" or "read_failed" or "shell_error" or "shell_closed" or "write_failed" or "health_timeout" or "resize_failed" or "connect_timeout" or "connect_failed";
    private void Announce(string message) => _telnet.Broadcast(Encoding.UTF8.GetBytes("\r\n[SSH Broker] " + message + "\r\n"));
    private async Task StopReconnectAsync()
    {
        Task pending;
        lock (_gate)
        {
            pending = _reconnectTask;
            _reconnectStop?.Cancel();
            if (!pending.IsCompleted && _state is "reconnecting" or "connecting") _connectionStop?.Cancel();
        }
        await pending.ConfigureAwait(false);
    }
    private async Task ReconnectLoopAsync(Task cleanup, CancellationTokenSource stop)
    {
        var token = stop.Token;
        try
        {
            await cleanup.ConfigureAwait(false);
            Announce("SSH connection lost. Recovering; input is disabled until connected.");
            while (!token.IsCancellationRequested)
            {
                int attempt, limit, delay;
                lock (_gate)
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    limit = _config.ReconnectAttempts;
                    if (_reconnectAttempt >= limit) break;
                    attempt = ++_reconnectAttempt; delay = _config.ReconnectDelayMs;
                    _state = "reconnecting";
                    EventLocked("reconnect_scheduled", $"attempt={attempt}/{limit}; delay_ms={delay}; cause={_lastDisconnect?.Code}");
                }
                Announce($"Reconnect attempt {attempt}/{limit} in {delay} ms.");
                await Task.Delay(delay, token).ConfigureAwait(false);
                try
                {
                    lock (_gate) EventLocked("reconnect_attempt", $"attempt={attempt}/{limit}");
                    await ConnectCoreAsync(true, token).ConfigureAwait(false);
                    lock (_gate) EventLocked("reconnect_success", $"attempt={attempt}/{limit}; generation={_generation}; previous scrollback retained; new remote shell");
                    return;
                }
                catch (BrokerException ex)
                {
                    if (token.IsCancellationRequested) return;
                    lock (_gate) EventLocked("reconnect_failed", $"attempt={attempt}/{limit}; code={ex.Code}");
                    if (!Retryable(ex.Code)) break;
                }
            }
            if (token.IsCancellationRequested) return;
            lock (_gate)
            {
                _state = "faulted";
                EventLocked("reconnect_exhausted", $"attempts={_reconnectAttempt}; last_error={_error?.Code}; manual reconnect required");
            }
            Announce("Automatic recovery stopped. Reconnect manually when ready.");
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { await CleanupTransportAsync().ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate) { _state = "faulted"; _error = new("reconnect_failed", "Recovery stopped: " + Sanitize(ex.GetType().Name)); EventLocked("reconnect_error", _error.Message); }
            await _telnet.StopAsync(preserveHistory: true).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_reconnectStop, stop)) { _recovering = false; _reconnectStop = null; _revision++; }
            }
            stop.Dispose();
        }
    }
}
