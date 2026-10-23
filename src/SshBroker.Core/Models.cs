using System.Text.Json;
using System.Text.Json.Serialization;

namespace SshBroker.Core;

public static class Wire
{
    public const int MaxLineBytes = 2 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    public static JsonElement Element(object? value = null) => JsonSerializer.SerializeToElement(value ?? new { }, Json);
    public static string String(JsonElement data, string name, string fallback = "") =>
        data.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
    public static int Int(JsonElement data, string name, int fallback) =>
        data.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    public static bool Bool(JsonElement data, string name, bool fallback) =>
        data.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
}

public sealed class BrokerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record BrokerConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "";
    public string Authentication { get; init; } = "password";
    public string PrivateKeyPath { get; init; } = "";
    public string HostKeyFingerprint { get; init; } = "";
    public bool TelnetEnabled { get; init; } = true;
    public int TelnetPort { get; init; } = 7001;
    public int Columns { get; init; } = 120;
    public int Rows { get; init; } = 32;
    public int ConnectTimeoutMs { get; init; } = 8000;
    public int OperationTimeoutMs { get; init; } = 5000;
    public int KeepAliveSeconds { get; init; } = 2;
    public int HealthTimeoutMs { get; init; } = 4000;
    public string HealthCommand { get; init; } = "true";
    public string LogRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public long LogLimitBytes { get; init; } = 16 * 1024 * 1024;

    public void Validate()
    {
        if (Host is null || Username is null || Authentication is null || PrivateKeyPath is null ||
            HostKeyFingerprint is null || HealthCommand is null || LogRoot is null)
            throw new BrokerException("invalid_config", "Configuration strings cannot be null.");
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsControl))
            throw new BrokerException("invalid_config", "Host must be a hostname or IP address.");
        if (Port is < 1 or > 65535 || TelnetPort is < 0 or > 65535)
            throw new BrokerException("invalid_config", "Invalid port; Telnet 0 requests an ephemeral loopback port.");
        if (Username.Length > 128 || Username.Any(char.IsControl) || Authentication is not ("password" or "key"))
            throw new BrokerException("invalid_config", "Invalid username or authentication mode.");
        if (Columns is < 20 or > 500 || Rows is < 5 or > 200)
            throw new BrokerException("invalid_config", "PTY dimensions must be 20..500 columns and 5..200 rows.");
        if (ConnectTimeoutMs is < 200 or > 120000 || OperationTimeoutMs is < 200 or > 120000 ||
            KeepAliveSeconds is < 1 or > 60 || HealthTimeoutMs is < 200 or > 120000)
            throw new BrokerException("invalid_config", "Timeout or keepalive interval outside supported range.");
        if (string.IsNullOrWhiteSpace(LogRoot) || LogLimitBytes is < 1024 or > 1024L * 1024 * 1024)
            throw new BrokerException("invalid_config", "A writable log root and 1 KiB..1 GiB log limit are required.");
        if (HostKeyFingerprint.Length > 128 || (!string.IsNullOrEmpty(HostKeyFingerprint) &&
            !HostKeyFingerprint.StartsWith("SHA256:", StringComparison.Ordinal)))
            throw new BrokerException("invalid_config", "Host key fingerprint must use SHA256:base64 form.");
    }
}

public sealed record ClientSnapshot(string Id, string Kind, bool CanWrite, long TxBytes);
public sealed record ErrorInfo(string Code, string Message);
public sealed record BrokerSnapshot
{
    public long Revision { get; init; }
    public long ConfigRevision { get; init; }
    public long Generation { get; init; }
    public string State { get; init; } = "disconnected";
    public string Policy { get; init; } = "shared";
    public string Writer { get; init; } = "";
    public BrokerConfig Config { get; init; } = new();
    public bool HasPassword { get; init; }
    public bool HasKeyPassphrase { get; init; }
    public string ObservedHostKey { get; init; } = "";
    public ErrorInfo? LastError { get; init; }
    public ClientSnapshot[] Clients { get; init; } = [];
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
    public long OutputCursor { get; init; }
    public bool TelnetListening { get; init; }
    public int LocalTelnetPort { get; init; }
    public string LogDirectory { get; init; } = "";
    public bool Logging { get; init; }
    public string LogFailure { get; init; } = "";
    public static BrokerSnapshot Initial() => new();
}

public sealed record OutputEntry(long Sequence, DateTimeOffset Time, string Direction, string Client,
    string Text, string Base64);
public sealed record OutputPage(OutputEntry[] Entries, long Next, bool LostHistory);
public sealed record RpcRequest(string Id, string Method, JsonElement Params)
{
    public static RpcRequest Create(string method, object? args = null) => new(Guid.NewGuid().ToString("N"), method, Wire.Element(args));
}
public sealed record RpcResponse(string Id, bool Ok, string Code, string Message, object? Result, BrokerSnapshot Snapshot);
