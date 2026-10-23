using System.Text;
using System.Text.Json;

namespace SshBroker.Core;

public sealed class SessionLog : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _rx, _tx, _events;
    private long _limit;
    public string DirectoryPath { get; private set; } = "";
    public bool Active { get; private set; }
    public string Failure { get; private set; } = "";
    public void Open(string root, long generation, long limit)
    {
        lock (_gate)
        {
            Close(); _limit = limit; Failure = "";
            DirectoryPath = Path.GetFullPath(Path.Combine(root,
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-g{generation}-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(DirectoryPath);
            try
            {
                _rx = File.Open(Path.Combine(DirectoryPath, "rx.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _tx = File.Open(Path.Combine(DirectoryPath, "tx.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _events = File.Open(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                Active = true;
            }
            catch { Close(); throw; }
        }
    }
    public bool Data(bool tx, byte[] data)
    {
        lock (_gate)
        {
            if (!Active) return false;
            return Write(tx ? _tx! : _rx!, data);
        }
    }
    public bool Event(string kind, string message, long revision, long generation)
    {
        lock (_gate)
        {
            if (!Active) return false;
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { time = DateTimeOffset.UtcNow, kind, message, revision, generation }, Wire.Json) + "\n");
            return Write(_events!, data);
        }
    }
    private bool Write(FileStream stream, byte[] data)
    {
        try
        {
            if (stream.Length + data.Length > _limit) throw new IOException("Per-file log size limit reached.");
            stream.Write(data); stream.Flush(); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failure = e.Message; Active = false; return false;
        }
    }
    private void Close()
    {
        Active = false;
        _rx?.Dispose(); _tx?.Dispose(); _events?.Dispose(); _rx = _tx = _events = null;
    }
    public void Dispose() { lock (_gate) Close(); }
}
