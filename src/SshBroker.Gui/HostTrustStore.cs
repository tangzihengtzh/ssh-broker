using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SshBroker.Gui;

internal static class HostTrustStore
{
    internal static string? DirectoryOverride { get; set; }

    private static string KeyPath(string host, int port)
    {
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{host.ToLowerInvariant()}:{port}")));
        return Path.Combine(DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshBroker", "reconnect-preview", "known-hosts"), name + ".txt");
    }
    public static string Load(string host, int port)
    {
        try { return File.ReadAllText(KeyPath(host, port)).Trim(); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
    public static void Save(string host, int port, string fingerprint)
    {
        string path = KeyPath(host, port);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, fingerprint); File.Move(temp, path, true);
    }
    public static bool MatchesOpenSsh(string host, int port, string fingerprint)
    {
        try
        {
            string target = port == 22 ? host : $"[{host}]:{port}";
            foreach (var line in File.ReadLines(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts")))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 || !fields[0].Split(',').Contains(target, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    string candidate = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(fields[2]))).TrimEnd('=');
                    if (candidate == fingerprint) return true;
                }
                catch (FormatException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
}
