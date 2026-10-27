namespace SshBroker.Core;

public sealed record WorkspaceSession(string Id, string Pipe, string Name, BrokerSnapshot Snapshot);

public sealed class WorkspaceRuntime : IAsyncDisposable
{
    private sealed class Entry(string id, string pipe, string name, BrokerRuntime runtime)
    {
        public string Id { get; } = id;
        public string Pipe { get; } = pipe;
        public string Name { get; set; } = name;
        public BrokerRuntime Runtime { get; } = runtime;
        public WorkspaceSession Describe() => new(Id, Pipe, Name, Runtime.Engine.Snapshot());
    }
    private readonly Dictionary<string, Entry> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PipeRpcServer _server;
    private readonly string _pipe;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    public ConnectionHistory History { get; }
    public Task Shutdown => _shutdown.Task;
    public event Action? ShowWindowRequested;
    public WorkspaceRuntime(string pipe, string directory)
    {
        if (pipe.Length > 65) throw new BrokerException("invalid_pipe", "Workspace pipe must be at most 65 characters.");
        _pipe = pipe; History = new(directory); _server = new(pipe, ExecuteAsync);
    }
    public void Start() => _server.Start();
    public async Task<RpcResponse> ExecuteAsync(RpcRequest request, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new BrokerException("service_stopped", "Workspace is shutting down.");
            object? result;
            switch (request.Method)
            {
                case "show-window": result = new { shown = ShowWindowRequested != null }; ShowWindowRequested?.Invoke(); break;
                case "help": result = new { kind = "workspace", commands = new[] { "sessions", "session-create", "session-close", "session-rename", "history", "palette", "shutdown" } }; break;
                case "status":
                case "sessions": result = _sessions.Values.Select(s => s.Describe()).ToArray(); break;
                case "session-create":
                {
                    string id = Guid.NewGuid().ToString("N")[..12], pipe = _pipe + "." + id;
                    string name = Name(request);
                    var runtime = new BrokerRuntime(pipe);
                    try
                    {
                        int port = History.Suggest("", 22, _sessions.Values.Select(s => s.Runtime.Engine.Snapshot().Config.TelnetPort));
                        await runtime.Engine.ConfigureAsync(Wire.Element(new { telnetPort = port }), token).ConfigureAwait(false);
                        var entry = new Entry(id, pipe, name, runtime);
                        runtime.Dispatcher.Configured += snapshot => History.Remember(snapshot.Config, entry.Name);
                        runtime.Start(); _sessions.Add(id, entry);
                        _ = runtime.Shutdown.ContinueWith(async _ => await ExecuteAsync(RpcRequest.Create("session-close", new { id })).ConfigureAwait(false)).Unwrap();
                        result = entry.Describe();
                    }
                    catch { await runtime.DisposeAsync().ConfigureAwait(false); throw; }
                    break;
                }
                case "session-close":
                {
                    var entry = Find(request); _sessions.Remove(entry.Id);
                    await entry.Runtime.DisposeAsync().ConfigureAwait(false); result = new { closed = entry.Id }; break;
                }
                case "session-rename":
                {
                    var entry = Find(request); entry.Name = Name(request);
                    History.Remember(entry.Runtime.Engine.Snapshot().Config, entry.Name); result = entry.Describe(); break;
                }
                case "history":
                {
                    string exclude = Wire.String(request.Params, "exclude");
                    result = new { preferences = History.Read(), failure = History.Failure,
                        suggestedPort = History.Suggest(Wire.String(request.Params, "host"), Wire.Int(request.Params, "port", 22), _sessions.Values.Where(s => s.Id != exclude).Select(s => s.Runtime.Engine.Snapshot().Config.TelnetPort)) }; break;
                }
                case "palette": History.SetPalette(Wire.String(request.Params, "value")); result = History.Read(); break;
                case "shutdown":
                    _ = Task.Run(async () => { await Task.Delay(150).ConfigureAwait(false); _shutdown.TrySetResult(); }); result = new { stopping = true }; break;
                default: throw new BrokerException("unknown_command", "Workspace command required. Use sessions to discover individual session pipes.");
            }
            return new(request.Id, true, "ok", "", result, BrokerSnapshot.Initial());
        }
        catch (Exception e)
        { return new(request.Id, false, e is BrokerException b ? b.Code : "workspace_error", e.Message, null, BrokerSnapshot.Initial()); }
        finally { _gate.Release(); }
    }
    private static string Name(RpcRequest request)
    {
        string name = Wire.String(request.Params, "name");
        if (name.Length > 128) throw new BrokerException("invalid_params", "Name must be at most 128 characters.");
        return name.Trim();
    }
    private Entry Find(RpcRequest request) => _sessions.TryGetValue(Wire.String(request.Params, "id"), out var entry) ? entry
        : throw new BrokerException("session_not_found", "Session does not exist.");
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            foreach (var entry in _sessions.Values) await entry.Runtime.DisposeAsync().ConfigureAwait(false);
            _sessions.Clear();
        }
        finally { _gate.Release(); }
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
