using System.Text.Json;
using SshBroker.Core;
using Xunit;

namespace SshBroker.Tests;

public class WorkspaceTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Warm")]
    [InlineData("Blue")]
    public void GlobalThemeSurvivesRestart(string palette)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SshBroker-theme-" + Guid.NewGuid().ToString("N"));
        new ConnectionHistory(directory).SetPalette(palette);
        Assert.Equal(palette, new ConnectionHistory(directory).Read().Palette);
    }
    private static JsonElement Result(RpcResponse response) => Wire.Element(response.Result);
    [Fact]
    public void HistoryPersistsOnlyProfileFieldsAndKeepsPortsAdvisory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SshBroker-history-" + Guid.NewGuid().ToString("N"));
        var store = new ConnectionHistory(directory);
        var c = new BrokerConfig { Host = "vm.example", Username = "builder", TelnetPort = 7101 };
        store.Remember(c, "开发设备");
        store.Remember(c with { Host = "board.example", TelnetPort = 7102 }, "开发设备");
        store.Remember(c with { TelnetPort = 7110 }, "虚拟机");
        store.SetPalette("Light");
        var restored = new ConnectionHistory(directory);
        Assert.Equal(2, restored.Read().History.Length);
        Assert.Equal("Light", restored.Read().Palette);
        Assert.Equal(7110, restored.Suggest("VM.EXAMPLE", 22, [7110]));
        int proposed = restored.Suggest("new.example", 22, [7001, 7002]);
        Assert.DoesNotContain(proposed, new[] { 7001, 7002, 7102, 7110 });
        string json = File.ReadAllText(Path.Combine(directory, "preferences.json"));
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task WorkspaceCreatesIndependentPipesAndConfigurationsAndAllowsDuplicateNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "SshBroker-workspace-" + Guid.NewGuid().ToString("N"));
        await using var workspace = new WorkspaceRuntime("test.workspace." + Guid.NewGuid().ToString("N"), root);
        workspace.Start();
        Assert.Empty(Result(await workspace.ExecuteAsync(RpcRequest.Create("sessions"))).EnumerateArray());
        var a = Result(await workspace.ExecuteAsync(RpcRequest.Create("session-create", new { name = "开发设备" })));
        var b = Result(await workspace.ExecuteAsync(RpcRequest.Create("session-create", new { name = "开发设备" })));
        string pipeA = a.GetProperty("pipe").GetString()!, pipeB = b.GetProperty("pipe").GetString()!;
        Assert.NotEqual(pipeA, pipeB);
        var ca = await RpcClient.CallAsync(pipeA, RpcRequest.Create("configure", new { host = "vm.example", username = "builder", password = "private-memory-only" }));
        var cb = await RpcClient.CallAsync(pipeB, RpcRequest.Create("configure", new { host = "board.example", username = "root" }));
        Assert.True(ca.Ok); Assert.True(cb.Ok);
        Assert.NotEqual(ca.Snapshot.Config.TelnetPort, cb.Snapshot.Config.TelnetPort);
        Assert.True(ca.Snapshot.HasPassword); Assert.False(cb.Snapshot.HasPassword);
        Assert.Equal("vm.example", (await RpcClient.CallAsync(pipeA, RpcRequest.Create("status"))).Snapshot.Config.Host);
        var closed = await workspace.ExecuteAsync(RpcRequest.Create("session-close", new { id = a.GetProperty("id").GetString() }));
        Assert.True(closed.Ok);
        Assert.True((await RpcClient.CallAsync(pipeB, RpcRequest.Create("status"))).Ok);
        Assert.Single(Result(await workspace.ExecuteAsync(RpcRequest.Create("sessions"))).EnumerateArray());
        Assert.DoesNotContain("private-memory-only", File.ReadAllText(Path.Combine(root, "preferences.json")));
    }
}
