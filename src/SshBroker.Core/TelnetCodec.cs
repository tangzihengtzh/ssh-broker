namespace SshBroker.Core;

public sealed class TelnetCodec
{
    private int _state;
    private byte _command;
    private bool _afterCr, _binary;
    private readonly bool[] _local = new bool[256], _remote = new bool[256];
    public TelnetCodec() { _local[0] = _local[1] = _local[3] = true; _remote[0] = _remote[3] = true; }
    public static byte[] Greeting => [255,251,0,255,253,0,255,251,1,255,251,3,255,253,3];
    public static byte[] Escape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        foreach (var b in data) { output.Add(b); if (b == 255) output.Add(b); }
        return output.ToArray();
    }
    public (byte[] Data, byte[] Reply) Decode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(); var reply = new List<byte>();
        foreach (var b in data)
        {
            switch (_state)
            {
                case 0:
                    if (b == 255) _state = 1;
                    else { if (!(_afterCr && b == 0 && !_binary)) output.Add(b); _afterCr = b == 13; }
                    break;
                case 1:
                    if (b == 255) { output.Add(b); _state = 0; _afterCr = false; }
                    else if (b is >= 251 and <= 254) { _command = b; _state = 2; }
                    else _state = b == 250 ? 3 : 0;
                    break;
                case 2:
                    if (_command == 253)
                    {
                        if (b is not (0 or 1 or 3)) reply.AddRange([255,252,b]);
                        else if (!_local[b]) { _local[b] = true; reply.AddRange([255,251,b]); }
                    }
                    else if (_command == 254 && _local[b]) { _local[b] = false; reply.AddRange([255,252,b]); }
                    else if (_command == 251)
                    {
                        if (b == 0) _binary = true;
                        if (b is not (0 or 3)) reply.AddRange([255,254,b]);
                        else if (!_remote[b]) { _remote[b] = true; reply.AddRange([255,253,b]); }
                    }
                    else if (_command == 252)
                    {
                        if (b == 0) _binary = false;
                        if (_remote[b]) { _remote[b] = false; reply.AddRange([255,254,b]); }
                    }
                    _state = 0; break;
                case 3: if (b == 255) _state = 4; break;
                case 4: _state = b == 240 ? 0 : 3; break;
            }
        }
        return (output.ToArray(), reply.ToArray());
    }
}
