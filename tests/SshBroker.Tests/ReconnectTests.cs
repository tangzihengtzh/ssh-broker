using SshBroker.Core;
using Xunit;

namespace SshBroker.Tests;

public class ReconnectTests
{
    [Fact]
    public async Task MaximumEscapedHistoryCanBeReplayedToNewTerminal()
    {
        var frontend = new TelnetFrontEnd((_, _) => { }, (_, _) => { }, (_, _) => Task.CompletedTask, _ => { });
        try
        {
            frontend.Start(0);
            frontend.Broadcast(Enumerable.Repeat((byte)255, 512 * 1024).ToArray());
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync("127.0.0.1", frontend.Port);
            var decoder = new TelnetCodec(); var buffer = new byte[8192]; int received = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (received < 512 * 1024)
            {
                int count = await client.GetStream().ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                received += decoder.Decode(buffer.AsSpan(0, count)).Data.Length;
            }
            Assert.Equal(512 * 1024, received);
        }
        finally { await frontend.StopAsync(); }
    }
    [Fact]
    public void DefaultRetryPolicyIsBounded()
    {
        var config = new BrokerConfig();
        Assert.Equal(3, config.ReconnectAttempts); Assert.Equal(2000, config.ReconnectDelayMs);
    }
    [Theory]
    [InlineData(-1, 2000)]
    [InlineData(21, 2000)]
    [InlineData(3, 199)]
    [InlineData(3, 60001)]
    public void InvalidRetrySettingsAreRejected(int attempts, int delay)
    {
        var config = new BrokerConfig { ReconnectAttempts = attempts, ReconnectDelayMs = delay };
        Assert.Throws<BrokerException>(config.Validate);
    }
    [Fact]
    public void CliCanConfigureAndDisableRecovery()
    {
        var request = CommandParser.Parse(["configure", "--reconnect-attempts", "0", "--reconnect-delay", "500"]);
        Assert.Equal(0, request.Params.GetProperty("reconnectAttempts").GetInt32());
        Assert.Equal(500, request.Params.GetProperty("reconnectDelayMs").GetInt32());
    }
    [Fact]
    public async Task OfflineInputIsRejectedAndAuditedWithoutPayload()
    {
        await using var engine = new BrokerEngine();
        var error = await Assert.ThrowsAsync<BrokerException>(() => engine.SendAsync("agent", System.Text.Encoding.UTF8.GetBytes("do-not-replay")));
        Assert.Equal("not_connected", error.Code);
        var output = engine.Output(0);
        Assert.Contains(output.Entries, e => e.Client == "offline_input_dropped");
        Assert.DoesNotContain(output.Entries, e => e.Text.Contains("do-not-replay"));
        Assert.False(engine.Snapshot().AutoReconnectActive);
    }
    [Fact]
    public async Task ShortPasswordDoesNotDestroyDiagnosticWords()
    {
        await using var engine = new BrokerEngine();
        await engine.ConfigureAsync(Wire.Element(new { password = "t" }));
        engine.Audit("connect", "connect_timeout");
        engine.Audit("t", "ok");
        var output = engine.Output(0);
        Assert.Contains(output.Entries, e => e.Text.Contains("method=connect code=connect_timeout"));
        Assert.Contains(output.Entries, e => e.Text.Contains("method=[REDACTED] code=ok"));
    }
}
