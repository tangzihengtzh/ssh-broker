using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SshBroker.Core;

// This exact projection is bound by WPF and also inspected by CLI/integration tests.
public sealed class SnapshotProjection : INotifyPropertyChanged
{
    private BrokerSnapshot _snapshot = BrokerSnapshot.Initial();
    public event PropertyChangedEventHandler? PropertyChanged;
    public BrokerSnapshot Snapshot => _snapshot;
    public long Revision => _snapshot.Revision;
    public long ConfigRevision => _snapshot.ConfigRevision;
    public long Generation => _snapshot.Generation;
    public string State => _snapshot.State;
    public string Host => _snapshot.Config.Host;
    public int Port => _snapshot.Config.Port;
    public string Username => _snapshot.Config.Username;
    public string Policy => _snapshot.Policy;
    public string Writer => _snapshot.Writer;
    public long RxBytes => _snapshot.RxBytes;
    public long TxBytes => _snapshot.TxBytes;
    public string Error => _snapshot.LastError is { } e ? $"{e.Code}: {e.Message}" : "";
    public string Fingerprint => _snapshot.ObservedHostKey;
    public string LogDirectory => _snapshot.LogDirectory;
    public bool Logging => _snapshot.Logging;
    public int LocalTelnetPort => _snapshot.LocalTelnetPort;
    public ClientSnapshot[] Clients => _snapshot.Clients;

    public bool Apply(BrokerSnapshot snapshot)
    {
        if (snapshot.Revision < _snapshot.Revision) return false;
        _snapshot = snapshot;
        Changed(string.Empty);
        return true;
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
