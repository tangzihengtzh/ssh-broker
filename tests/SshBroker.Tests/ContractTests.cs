using SshBroker.Core;
using Xunit;

namespace SshBroker.Tests;

public class ContractTests
{
    [Fact]
    public void DefaultPolicyAndProjectionAgree()
    {
        var snapshot = BrokerSnapshot.Initial();
        var projection = new SnapshotProjection();
        projection.Apply(snapshot);
        Assert.Equal("shared", snapshot.Policy);
        Assert.Equal(snapshot.Revision, projection.Revision);
        Assert.Equal(snapshot.Config.Host, projection.Host);
        Assert.Equal(snapshot.State, projection.State);
    }

    [Fact]
    public void QuotedCommandsPreserveWhitespaceAndEscapes()
    {
        Assert.Equal(new[] { "send", "--text", "hello world\n", "--cr" },
            ScriptTokenizer.Tokenize("send --text \"hello world\\n\" --cr"));
        Assert.Equal(new[] { "configure", "--key", @"C:\ssh keys\id_ed25519" },
            ScriptTokenizer.Tokenize("configure --key {C:\\ssh keys\\id_ed25519}"));
    }

    [Theory]
    [InlineData("send \"unterminated")]
    [InlineData("configure {unterminated")]
    public void BadScriptFailsClearly(string command)
    {
        Assert.Throws<BrokerException>(() => ScriptTokenizer.Tokenize(command));
    }
}
