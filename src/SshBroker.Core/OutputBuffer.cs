using System.Text;
using System.Text.Json;

namespace SshBroker.Core;

public sealed class OutputBuffer(int maxEntries = 512, int maxBytes = 1024 * 1024)
{
    private readonly object _gate = new();
    private readonly Queue<OutputEntry> _entries = new();
    private long _cursor;
    private int _bytes;
    public long Cursor { get { lock (_gate) return _cursor; } }
    public OutputEntry Add(string direction, string client, byte[] bytes, string? text = null)
    {
        lock (_gate)
        {
            var entry = new OutputEntry(++_cursor, DateTimeOffset.UtcNow, direction, client,
                text ?? Encoding.UTF8.GetString(bytes), Convert.ToBase64String(bytes));
            _entries.Enqueue(entry); _bytes += Cost(entry);
            while (_entries.Count > maxEntries || _bytes > maxBytes)
                _bytes -= Cost(_entries.Dequeue());
            return entry;
        }
    }
    public OutputPage Read(long after, int limit = 100)
    {
        if (after < 0 || limit is < 1 or > 200) throw new BrokerException("invalid_params", "Invalid output cursor or page size.");
        lock (_gate)
        {
            if (after > _cursor) throw new BrokerException("invalid_cursor", "Cursor belongs to a newer/different service instance.");
            var selected = new List<OutputEntry>(); int encodedBytes = 0;
            foreach (var entry in _entries.Where(e => e.Sequence > after).Take(limit))
            {
                int size = JsonSerializer.SerializeToUtf8Bytes(entry, Wire.Json).Length;
                if (selected.Count > 0 && encodedBytes + size > 512 * 1024) break;
                selected.Add(entry); encodedBytes += size;
            }
            var entries = selected.ToArray();
            return new(entries, entries.Length > 0 ? entries[^1].Sequence : after,
                _entries.TryPeek(out var first) && after < first.Sequence - 1);
        }
    }
    private static int Cost(OutputEntry entry) => entry.Text.Length * 2 + entry.Base64.Length * 2 + 128;
}
