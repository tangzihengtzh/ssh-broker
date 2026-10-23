using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace SshBroker.Core;

public sealed class TelnetFrontEnd(
    Action<string, string> connected,
    Action<string, string> disconnected,
    Func<string, byte[], Task> send,
    Action<string> error)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Peer> _peers = new();
    private readonly List<byte> _history = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _accept;
    private int _next;
    public bool Listening => _listener != null;
    public int Port { get; private set; }
    public void Start(int port)
    {
        if (_listener != null) throw new InvalidOperationException("Telnet already started.");
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start(32); _listener = listener; Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _stop = new(); _history.Clear();
        _accept = AcceptAsync(listener, _stop.Token);
    }
    public void Broadcast(byte[] data)
    {
        lock (_gate)
        {
            _history.AddRange(data);
            if (_history.Count > 65536) _history.RemoveRange(0, _history.Count - 65536);
            var packet = TelnetCodec.Escape(data);
            foreach (var peer in _peers.Values.ToArray()) peer.Enqueue(packet);
        }
    }
    private async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); tcp.NoDelay = true;
                var id = $"telnet-{Interlocked.Increment(ref _next):D4}";
                var peer = new Peer(id, tcp, send, (id, reason) =>
                {
                    lock (_gate) _peers.Remove(id);
                    disconnected(id, reason);
                });
                lock (_gate)
                {
                    if (_peers.Count >= 32 || token.IsCancellationRequested) { tcp.Dispose(); continue; }
                    try { connected(id, "telnet"); }
                    catch (BrokerException) { tcp.Dispose(); continue; }
                    _peers.Add(id, peer);
                    peer.Enqueue(TelnetCodec.Greeting);
                    if (_history.Count > 0) peer.Enqueue(TelnetCodec.Escape(_history.ToArray()));
                    peer.Start();
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested) error("Telnet listener: " + e.Message);
        }
    }
    public async Task StopAsync()
    {
        _stop?.Cancel();
        var listener = _listener; _listener = null; Port = 0; listener?.Stop();
        if (_accept != null) { await _accept.ConfigureAwait(false); _accept = null; }
        Peer[] peers;
        lock (_gate) { peers = _peers.Values.ToArray(); _history.Clear(); }
        foreach (var peer in peers) peer.Stop("server_stop");
        await Task.WhenAll(peers.Select(p => p.Completion)).ConfigureAwait(false);
        lock (_gate) _peers.Clear();
        _stop?.Dispose(); _stop = null;
    }
    public void Disconnect(string id)
    {
        Peer? peer;
        lock (_gate) _peers.TryGetValue(id, out peer);
        peer?.Stop("requested");
    }
    private sealed class Peer(string id, TcpClient tcp, Func<string, byte[], Task> send, Action<string, string> closed)
    {
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _stop = new();
        private long _bytes;
        private int _stopped;
        private string _reason = "";
        public Task Completion { get; private set; } = Task.CompletedTask;
        public void Start() => Completion = RunAsync();
        public void Enqueue(byte[] packet)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            // Leave room for ordinary LAN bursts/GC pauses while retaining a hard per-peer bound.
            if (Interlocked.Add(ref _bytes, packet.Length) > 1024 * 1024 || !_queue.Writer.TryWrite(packet)) Stop("slow_client_queue_overflow");
        }
        public void Stop(string reason)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            _reason = reason;
            _stop.Cancel(); tcp.Dispose(); _queue.Writer.TryComplete();
        }
        private async Task RunAsync()
        {
            var input = ReceiveAsync(); var output = TransmitAsync();
            try
            {
                var finished = await Task.WhenAny(input, output).ConfigureAwait(false);
                Stop(finished == input ? (input.IsCompletedSuccessfully ? "peer_closed" : "socket_read_failed") : "socket_write_failed_or_timeout");
            }
            finally
            {
                Stop("session_stopped");
                try { await Task.WhenAll(input, output).ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
                closed(id, _reason); _stop.Dispose();
            }
        }
        private async Task ReceiveAsync()
        {
            var codec = new TelnetCodec(); var data = new byte[4096]; var stream = tcp.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(data, _stop.Token).ConfigureAwait(false);
                if (count == 0) break;
                var decoded = codec.Decode(data.AsSpan(0, count));
                if (decoded.Reply.Length > 0) Enqueue(decoded.Reply);
                if (decoded.Data.Length > 0)
                {
                    try { await send(id, decoded.Data).ConfigureAwait(false); }
                    catch (BrokerException) { /* Denial is visible in the GUI/control event stream. */ }
                }
            }
        }
        private async Task TransmitAsync()
        {
            var stream = tcp.GetStream();
            await foreach (var packet in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                Interlocked.Add(ref _bytes, -packet.Length);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(3000);
                await stream.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
            }
        }
    }
}
