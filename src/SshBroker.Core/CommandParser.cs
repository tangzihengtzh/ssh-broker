using System.Globalization;
using System.Text;

namespace SshBroker.Core;

public static class CommandParser
{
    private static readonly Dictionary<string, string> ConfigOptions = new()
    {
        ["host"] = "host", ["port"] = "port", ["user"] = "username", ["auth"] = "authentication",
        ["key"] = "privateKeyPath", ["fingerprint"] = "hostKeyFingerprint", ["telnet-port"] = "telnetPort",
        ["telnet-enabled"] = "telnetEnabled", ["columns"] = "columns", ["rows"] = "rows",
        ["connect-timeout"] = "connectTimeoutMs", ["operation-timeout"] = "operationTimeoutMs",
        ["keepalive"] = "keepAliveSeconds", ["health-timeout"] = "healthTimeoutMs", ["health-command"] = "healthCommand",
        ["log-root"] = "logRoot", ["log-limit"] = "logLimitBytes", ["expected-revision"] = "expectedRevision",
        ["reconnect-attempts"] = "reconnectAttempts", ["reconnect-delay"] = "reconnectDelayMs"
    };
    private static readonly HashSet<string> Numeric = ["port", "telnetPort", "columns", "rows", "connectTimeoutMs",
        "operationTimeoutMs", "keepAliveSeconds", "healthTimeoutMs", "logLimitBytes", "expectedRevision", "reconnectAttempts", "reconnectDelayMs"];

    public static RpcRequest Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return RpcRequest.Create("help");
        string method = args[0]; var values = new Dictionary<string, object>();
        if (method == "policy")
        {
            if (args.Count != 2) throw Invalid("policy requires shared, single, or readonly.");
            return RpcRequest.Create(method, new { value = args[1] });
        }
        if (method is "status" or "help" or "connect" or "disconnect" or "reconnect" or "clients" or "logs" or "projection" or "shutdown" or "sessions")
        {
            if (args.Count != 1) throw Invalid(method + " does not accept arguments.");
            return RpcRequest.Create(method);
        }
        var options = ParseOptions(args.Skip(1).ToArray());
        if (method is "session-create" or "session-close" or "session-rename" or "history" or "palette")
        {
            CheckAllowed(options, "id", "name", "host", "port", "exclude", "value");
            foreach (var (name, value) in options) values[name] = name == "port" ? Number(value) : value;
        }
        else if (method == "configure")
        {
            foreach (var (name, text) in options)
            {
                if (name is "password-env" or "passphrase-env")
                {
                    var secret = Environment.GetEnvironmentVariable(text);
                    if (secret is null) throw new BrokerException("credentials_missing", "Environment variable is not set: " + text);
                    values[name == "password-env" ? "password" : "keyPassphrase"] = secret;
                }
                else if (name == "clear-password") values["password"] = "";
                else if (ConfigOptions.TryGetValue(name, out var field))
                    values[field] = Numeric.Contains(field) ? Number(text) : field == "telnetEnabled" ? Boolean(text) : text;
                else throw Invalid("Unknown configure option: --" + name);
            }
        }
        else if (method == "send")
        {
            CheckAllowed(options, "text", "hex", "base64", "client", "cr", "lf");
            if (new[] { "text", "hex", "base64" }.Count(options.ContainsKey) != 1)
                throw Invalid("send requires exactly one of --text, --hex or --base64.");
            byte[] data;
            try
            {
                data = options.TryGetValue("hex", out var hex) ? Convert.FromHexString(string.Concat(hex.Where(c => !char.IsWhiteSpace(c))))
                    : options.TryGetValue("base64", out var b64) ? Convert.FromBase64String(b64) : Encoding.UTF8.GetBytes(options["text"]);
            }
            catch (FormatException) { throw Invalid("Invalid hexadecimal or base64 payload."); }
            if (options.ContainsKey("cr")) data = [.. data, 13];
            if (options.ContainsKey("lf")) data = [.. data, 10];
            if (data.Length is < 1 or > 4096) throw Invalid("Send accepts 1..4096 bytes including line endings.");
            values["base64"] = Convert.ToBase64String(data); values["client"] = options.GetValueOrDefault("client", "agent");
        }
        else if (method is "acquire" or "release" or "client-close")
        { CheckAllowed(options, "client"); values["client"] = options.GetValueOrDefault("client", "agent"); }
        else if (method == "resize")
        {
            CheckAllowed(options, "columns", "rows");
            values["columns"] = Number(options.GetValueOrDefault("columns", "120")); values["rows"] = Number(options.GetValueOrDefault("rows", "32"));
        }
        else if (method is "output" or "wait" or "wait-output")
        {
            var allowed = method == "output" ? new[] { "after", "limit" } : method == "wait" ? ["state", "ms"] : ["contains", "after", "ms"];
            CheckAllowed(options, allowed);
            foreach (var (name, value) in options) values[name] = name is "after" or "limit" or "ms" ? Number(value) : value;
        }
        else throw new BrokerException("unknown_command", "Unknown command: " + method);
        return RpcRequest.Create(method, values);
    }
    public static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw Invalid("Expected an option, got: " + args[i]);
            var name = args[i][2..];
            if (options.ContainsKey(name)) throw Invalid("Duplicate option: --" + name);
            if (name is "cr" or "lf" or "clear-password") options.Add(name, "true");
            else if (++i < args.Count) options.Add(name, args[i]);
            else throw Invalid("Missing option value: --" + name);
        }
        return options;
    }
    private static long Number(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        ? number : throw Invalid("Expected a nonnegative integer.");
    private static bool Boolean(string value) => bool.TryParse(value, out var result) ? result : throw Invalid("Expected true or false.");
    private static void CheckAllowed(Dictionary<string, string> options, params string[] allowed)
    { foreach (var key in options.Keys) if (!allowed.Contains(key)) throw Invalid("Unknown option: --" + key); }
    private static BrokerException Invalid(string message) => new("invalid_params", message);
}
