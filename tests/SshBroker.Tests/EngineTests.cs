using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using SshBroker.Core;
using Xunit;

namespace SshBroker.Tests;

public sealed class EngineTests
{
    [Fact]
    public async Task ConfigurationIsAtomicAndSecretsNeverEnterSnapshots()
    {
        await using var engine = new BrokerEngine();
        var dispatcher = new CommandDispatcher(engine);
        var result = await dispatcher.ExecuteAsync(RpcRequest.Create("configure", new { host = "example.test", port = 2222, password = "unique-secret-xyz" }));
        Assert.True(result.Ok); Assert.True(result.Snapshot.HasPassword);
        Assert.DoesNotContain("unique-secret-xyz", JsonSerializer.Serialize(result, Wire.Json));
        var invalid = await dispatcher.ExecuteAsync(RpcRequest.Create("configure", new { host = "changed.test", port = -1 }));
        Assert.False(invalid.Ok); Assert.Equal("invalid_config", invalid.Code);
        Assert.Equal("example.test", engine.Snapshot().Config.Host);
        Assert.Equal(2222, engine.Snapshot().Config.Port);
        var nullField = await dispatcher.ExecuteAsync(new RpcRequest("null-field", "configure", JsonDocument.Parse("{\"host\":null}").RootElement.Clone()));
        Assert.Equal("invalid_config", nullField.Code); Assert.Equal("example.test", engine.Snapshot().Config.Host);
    }

    [Fact]
    public async Task StaleConfigurationRevisionIsRejected()
    {
        await using var engine = new BrokerEngine();
        await engine.ConfigureAsync(Wire.Element(new { host = "updated.test" }));
        var error = await Assert.ThrowsAsync<BrokerException>(() => engine.ConfigureAsync(Wire.Element(new { host = "stale.test", expectedRevision = 0 })));
        Assert.Equal("revision_conflict", error.Code);
        Assert.Equal("updated.test", engine.Snapshot().Config.Host);
    }

    [Fact]
    public async Task DuplicatePolicyIsIdempotent()
    {
        await using var engine = new BrokerEngine();
        engine.SetPolicy("single"); var revision = engine.Snapshot().Revision;
        engine.SetPolicy("single"); Assert.Equal(revision, engine.Snapshot().Revision);
        Assert.Throws<BrokerException>(() => engine.SetPolicy("invalid"));
        Assert.Equal("single", engine.Snapshot().Policy);
    }

    [Fact]
    public async Task DisconnectedWritesNeverEnterTheTransmitCounter()
    {
        await using var engine = new BrokerEngine();
        var e = await Assert.ThrowsAsync<BrokerException>(() => engine.SendAsync("test", [1,2,3]));
        Assert.Equal("not_connected", e.Code); Assert.Equal(0, engine.Snapshot().TxBytes);
        var oversized = await Assert.ThrowsAsync<BrokerException>(() => engine.SendAsync("test", new byte[4097]));
        Assert.Equal("invalid_params", oversized.Code);
    }

    [Fact]
    public async Task DispatcherDistinguishesTimeoutAndInvalidCommand()
    {
        await using var engine = new BrokerEngine(); var dispatcher = new CommandDispatcher(engine);
        var wait = await dispatcher.ExecuteAsync(RpcRequest.Create("wait", new { state = "connected", ms = 60 }));
        Assert.False(wait.Ok); Assert.Equal("wait_timeout", wait.Code);
        var bad = await dispatcher.ExecuteAsync(RpcRequest.Create("not-a-command"));
        Assert.Equal("unknown_command", bad.Code);
    }

    [Theory]
    [InlineData(0, 32)] [InlineData(120, 0)] [InlineData(501, 32)] [InlineData(120, 201)]
    public void InvalidPtySizesAreRejected(int columns, int rows)
    { Assert.Throws<BrokerException>(() => (new BrokerConfig { Columns = columns, Rows = rows }).Validate()); }

    [Fact]
    public void ProjectionIgnoresLateResponses()
    {
        var view = new SnapshotProjection();
        view.Apply(BrokerSnapshot.Initial() with { Revision = 10, State = "connected", Config = new() { Host = "new.test", Port = 2222 } });
        Assert.False(view.Apply(BrokerSnapshot.Initial() with { Revision = 9 }));
        Assert.Equal("connected", view.State); Assert.Equal("new.test", view.Host); Assert.Equal(2222, view.Port);
    }

    [Fact]
    public void OutputBufferReportsTruncationAndPreservesRawBytes()
    {
        var ring = new OutputBuffer(3, 1024 * 1024);
        for (int i = 0; i < 5; i++) ring.Add("rx", "test", [(byte)i, 255]);
        var page = ring.Read(0);
        Assert.True(page.LostHistory); Assert.Equal(3, page.Entries.Length); Assert.Equal(5, page.Next);
        Assert.Equal(new byte[] { 4,255 }, Convert.FromBase64String(page.Entries[^1].Base64));
        Assert.Throws<BrokerException>(() => ring.Read(6));
    }

    [Fact]
    public void OutputPagesStayBelowWireBudgetForControlBytes()
    {
        var ring = new OutputBuffer(512, 1024 * 1024);
        for (int i = 0; i < 100; i++) ring.Add("rx", "test", new byte[4096]);
        var page = ring.Read(0, 200);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, Wire.Json).Length < Wire.MaxLineBytes);
        Assert.NotEmpty(page.Entries);
    }

    [Fact]
    public async Task JsonLineReaderHandlesFragmentBoundariesAndMultipleFrames()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"one\"}\n{\"id\":\"two\"}\r\n"));
        var reader = new JsonLineReader(stream);
        Assert.Equal("{\"id\":\"one\"}", await reader.ReadAsync());
        Assert.Equal("{\"id\":\"two\"}", await reader.ReadAsync()); Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task OversizedOrIncompleteJsonLineIsRejected()
    {
        using var large = new MemoryStream(new byte[Wire.MaxLineBytes + 1]);
        Assert.Equal("frame_too_large", (await Assert.ThrowsAsync<BrokerException>(() => new JsonLineReader(large).ReadAsync())).Code);
        using var partial = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        Assert.Equal("invalid_frame", (await Assert.ThrowsAsync<BrokerException>(() => new JsonLineReader(partial).ReadAsync())).Code);
    }

    [Fact]
    public async Task RealPipeRecoversFromMalformedJsonAndKeepsFrameBoundaries()
    {
        string name = "SshBroker.unit." + Guid.NewGuid().ToString("N");
        await using var runtime = new BrokerRuntime(name); runtime.Start();
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        var valid = JsonSerializer.Serialize(RpcRequest.Create("status"), Wire.Json);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes("{bad}\n" + valid + "\n"), timeout.Token);
        var reader = new JsonLineReader(pipe);
        var bad = JsonSerializer.Deserialize<RpcResponse>((await reader.ReadAsync(timeout.Token))!, Wire.Json)!;
        var good = JsonSerializer.Deserialize<RpcResponse>((await reader.ReadAsync(timeout.Token))!, Wire.Json)!;
        Assert.False(bad.Ok); Assert.Equal("invalid_request", bad.Code);
        Assert.True(good.Ok); Assert.Equal("disconnected", good.Snapshot.State);
    }

    [Fact]
    public void CommandParserProducesExactBytesAndRejectsAmbiguity()
    {
        var request = CommandParser.Parse(["send", "--hex", "41 ff", "--cr", "--lf", "--client", "test"]);
        Assert.Equal(new byte[] { 65,255,13,10 }, Convert.FromBase64String(request.Params.GetProperty("base64").GetString()!));
        Assert.Throws<BrokerException>(() => CommandParser.Parse(["send", "--text", "hi", "--hex", "00"]));
        Assert.Throws<BrokerException>(() => CommandParser.Parse(["configure", "--password", "never-accept-password-argv"]));
    }

    [Fact]
    public void ScriptLiteralBracesDoNotExpandVariablesOrBackslashes()
    {
        Assert.Equal(new[] { "send", "--text", "$HOME; [exec dangerous] C:\\path" },
            ScriptTokenizer.Tokenize("send --text {$HOME; [exec dangerous] C:\\path}"));
        Assert.Empty(ScriptTokenizer.Tokenize(" # comment"));
        Assert.Equal(new[] { "send", "--text", "" }, ScriptTokenizer.Tokenize("send --text \"\""));
    }

    [Fact]
    public void TelnetCodecHandlesFragmentedNegotiationAndLiteralIac()
    {
        var codec = new TelnetCodec();
        Assert.Empty(codec.Decode([255,251]).Data);
        Assert.Empty(codec.Decode([0]).Data);
        Assert.Equal(new byte[] { 13,0,255 }, codec.Decode([13,0,255,255]).Data);
        var nvt = new TelnetCodec();
        Assert.Equal(new byte[] { 13 }, nvt.Decode([13,0]).Data);
        Assert.Equal(new byte[] { 1,255,255,2 }, TelnetCodec.Escape([1,255,2]));
        Assert.Empty(nvt.Decode([255,250,1,2,255,240]).Data);
    }

    [Fact]
    public void LogLimitDisablesLoggingWithoutCorruptingExistingData()
    {
        using var temp = new OwnedTemporaryDirectory(); using var log = new SessionLog();
        log.Open(temp.Path, 1, 1024);
        Assert.True(log.Data(false, new byte[800]));
        Assert.False(log.Data(false, new byte[300])); Assert.False(log.Active); Assert.NotEmpty(log.Failure);
        Assert.Equal(800, new FileInfo(System.IO.Path.Combine(log.DirectoryPath, "rx.bin")).Length);
    }

    private sealed class OwnedTemporaryDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SshBroker-unit-tests"));
        public string Path { get; }
        public OwnedTemporaryDirectory()
        { Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside owned test root.");
            Directory.Delete(resolved, true);
        }
    }
}
