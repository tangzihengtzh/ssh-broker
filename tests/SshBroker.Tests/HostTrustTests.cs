using System.Security.Cryptography;
using System.Text;
using SshBroker.Gui;
using Xunit;

namespace SshBroker.Tests;

public class HostTrustTests
{
    [Fact]
    public void RememberedIdentitySurvivesReloadAndIsBoundToHostAndPort()
    {
        string host = Guid.NewGuid().ToString("N") + ".invalid";
        try
        {
            Assert.Equal("", HostTrustStore.Load(host, 22));
            HostTrustStore.Save(host, 22, "SHA256:test-key");
            Assert.Equal("SHA256:test-key", HostTrustStore.Load(host.ToUpperInvariant(), 22));
            Assert.Equal("", HostTrustStore.Load(host, 2222));
            Assert.Equal("", HostTrustStore.Load("other-" + host, 22));
        }
        finally
        {
            string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host + ":22")));
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshBroker", "reconnect-preview", "known-hosts", name + ".txt"));
        }
    }
}
