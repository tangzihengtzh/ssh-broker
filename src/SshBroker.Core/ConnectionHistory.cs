using System.Net.NetworkInformation;
using System.Text.Json;

namespace SshBroker.Core;

public sealed record ConnectionProfile(string Host, int Port, string Username, string Name, int TelnetPort)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => $"{(Name.Length == 0 ? Host : Name)} — {Host}:{Port} · Telnet {TelnetPort}";
}
public sealed record UserPreferences(ConnectionProfile[] History, string Palette = "Light");

public sealed class ConnectionHistory
{
    private readonly object _gate = new();
    private readonly string _path;
    private UserPreferences _preferences = new([]);
    public string Failure { get; private set; } = "";
    public ConnectionHistory(string directory)
    {
        _path = Path.Combine(directory, "preferences.json");
        try
        {
            if (File.Exists(_path)) _preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(_path), Wire.Json) ?? new([]);
            _preferences = _preferences with { History = (_preferences.History ?? []).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Host) && p.Port is > 0 and <= 65535 && p.TelnetPort is >= 0 and <= 65535).Take(200).ToArray() };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Failure = "读取历史记录失败：" + e.Message; }
    }
    public UserPreferences Read() { lock (_gate) return _preferences with { History = _preferences.History.ToArray(), Palette = NormalizePalette(_preferences.Palette) }; }
    public void Remember(BrokerConfig config, string name)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || config.Username.Length == 0) return;
        lock (_gate)
        {
            var item = new ConnectionProfile(config.Host.Trim(), config.Port, config.Username, name, config.TelnetPort);
            _preferences = _preferences with { History = new[] { item }.Concat(_preferences.History.Where(p => !Same(p, item.Host, item.Port))).Take(200).ToArray() };
            Save();
        }
    }
    public static bool Same(ConnectionProfile p, string host, int port) => p.Host.Equals(host.Trim(), StringComparison.OrdinalIgnoreCase) && p.Port == port;
    public static readonly string[] Palettes = ["Light", "Dark", "Warm", "Blue"];
    public static string NormalizePalette(string palette) => palette switch
    { "Dark" or "Midnight" or "Dracula" or "Nord" => "Dark", "Warm" or "Solarized" => "Warm", "Blue" => "Blue", _ => "Light" };
    public void SetPalette(string palette)
    {
        if (!Palettes.Contains(palette) && palette is not ("Midnight" or "Dracula" or "Nord" or "Solarized")) throw new BrokerException("invalid_params", "Unknown palette.");
        palette = NormalizePalette(palette);
        lock (_gate) { _preferences = _preferences with { Palette = palette }; Save(); }
    }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_preferences, Wire.Json));
            File.Move(temporary, _path, true); Failure = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failure = "历史记录未保存：" + e.Message; }
    }
    public int Suggest(string host, int port, IEnumerable<int> reserved)
    {
        var data = Read();
        var busy = reserved.Concat(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(p => p.Port)).Where(p => p > 0).ToHashSet();
        var previous = data.History.FirstOrDefault(p => Same(p, host, port));
        // History is advisory: a known target recalls its last port even if currently busy.
        if (previous?.TelnetPort > 0) return previous.TelnetPort;
        busy.UnionWith(data.History.Select(p => p.TelnetPort));
        for (int candidate = 7001; candidate <= 65535; candidate++) if (!busy.Contains(candidate)) return candidate;
        throw new BrokerException("port_unavailable", "No available suggested Telnet port.");
    }
}
