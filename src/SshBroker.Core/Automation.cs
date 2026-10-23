using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SshBroker.Core;

public sealed class JsonLineReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _offset, _count;
    public async Task<string?> ReadAsync(CancellationToken token = default)
    {
        var line = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await stream.ReadAsync(_buffer, token).ConfigureAwait(false); _offset = 0;
                if (_count == 0) return line.WrittenCount == 0 ? null : throw new BrokerException("invalid_frame", "Incomplete JSON line.");
            }
            int newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            int take = (newline >= 0 ? newline : _count) - _offset;
            if (line.WrittenCount + take > Wire.MaxLineBytes) throw new BrokerException("frame_too_large", "JSON line exceeds 2 MiB.");
            line.Write(_buffer.AsSpan(_offset, take)); _offset += take;
            if (newline >= 0) { _offset++; return Encoding.UTF8.GetString(line.WrittenSpan).TrimEnd('\r'); }
        }
    }
}

public sealed class PipeRpcServer : IAsyncDisposable
{
    private readonly string _pipe;
    private readonly CommandDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, (NamedPipeServerStream Pipe, Task Task)> _clients = new();
    private readonly FileStream _guard;
    private Task? _accept; private long _id;
    public PipeRpcServer(string pipe, CommandDispatcher dispatcher)
    {
        ValidateName(pipe); _pipe = pipe; _dispatcher = dispatcher;
        var directory = Path.Combine(Path.GetTempPath(), "SshBroker-ipc"); Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pipe)));
        try { _guard = File.Open(Path.Combine(directory, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BrokerException("service_exists", "A broker with this pipe name is already running."); }
    }
    public static void ValidateName(string pipe)
    {
        if (pipe.Length is < 1 or > 100 || pipe.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new BrokerException("invalid_pipe", "Invalid pipe name.");
    }
    public void Start() => _accept = AcceptAsync();
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
                try { await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
                catch { server.Dispose(); throw; }
                if (_clients.Count >= 32) { server.Dispose(); continue; }
                var id = Interlocked.Increment(ref _id);
                var task = HandleAsync(server);
                _clients[id] = (server, task);
                _ = task.ContinueWith(_ => { _clients.TryRemove(id, out var ignored); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        try
        {
            var reader = new JsonLineReader(pipe);
            while (!_stop.IsCancellationRequested)
            {
                var line = await reader.ReadAsync(_stop.Token).ConfigureAwait(false);
                if (line == null) break;
                RpcResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Json);
                    if (request == null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.Method))
                        throw new JsonException();
                    response = await _dispatcher.ExecuteAsync(request, _stop.Token).ConfigureAwait(false);
                }
                catch (JsonException)
                { response = await _dispatcher.ExecuteAsync(RpcRequest.Create("__invalid_json__"), _stop.Token).ConfigureAwait(false); response = response with { Id = "", Code = "invalid_request", Message = "Invalid JSON request." }; }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json);
                if (bytes.Length > Wire.MaxLineBytes) bytes = JsonSerializer.SerializeToUtf8Bytes(response with
                    { Ok = false, Code = "response_too_large", Message = "Use a smaller output page.", Result = null }, Wire.Json);
                await pipe.WriteAsync(bytes, _stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { 10 }, _stop.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or BrokerException or TimeoutException) { }
        finally { pipe.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var peer in _clients.Values) peer.Pipe.Dispose();
        if (_accept != null) await _accept.ConfigureAwait(false);
        try { await Task.WhenAll(_clients.Values.Select(c => c.Task)).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        _guard.Dispose(); _stop.Dispose();
    }
}

public static class RpcClient
{
    public static async Task<RpcResponse> CallAsync(string pipeName, RpcRequest request, int timeoutMs = 15000, CancellationToken token = default)
    {
        PipeRpcServer.ValidateName(pipeName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeoutMs);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Wire.Json);
        if (bytes.Length > Wire.MaxLineBytes) throw new BrokerException("frame_too_large", "JSON request exceeds 2 MiB.");
        await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 10 }, deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        var line = await new JsonLineReader(pipe).ReadAsync(deadline.Token).ConfigureAwait(false);
        if (line == null) throw new BrokerException("service_disconnected", "Service closed before returning a response; a write may already have happened.");
        return JsonSerializer.Deserialize<RpcResponse>(line, Wire.Json) ?? throw new BrokerException("invalid_response", "Invalid service response.");
    }
}

public sealed class BrokerRuntime : IAsyncDisposable
{
    public BrokerEngine Engine { get; } = new();
    public CommandDispatcher Dispatcher { get; }
    private readonly PipeRpcServer _server;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Shutdown => _shutdown.Task;
    public BrokerRuntime(string pipe)
    {
        Dispatcher = new(Engine); _server = new(pipe, Dispatcher);
        Dispatcher.ShutdownRequested += () => _shutdown.TrySetResult();
    }
    public void Start() => _server.Start();
    public async ValueTask DisposeAsync()
    {
        // Cancel pending SSH operations before waiting for RPC handlers to finish.
        await Engine.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
