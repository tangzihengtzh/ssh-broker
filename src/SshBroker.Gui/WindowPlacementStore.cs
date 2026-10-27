using System.IO;
using System.Text.Json;
using System.Windows;

namespace SshBroker.Gui;

public sealed record WindowPlacement(double Width, double Height, double Left, double Top, bool Maximized);

public static class WindowPlacementStore
{
    public static WindowPlacement? Load(string directory)
    {
        try
        {
            var value = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(Path.Combine(directory, "window.json")));
            return value != null && double.IsFinite(value.Width) && double.IsFinite(value.Height) && double.IsFinite(value.Left) && double.IsFinite(value.Top)
                && value.Width > 0 && value.Height > 0 ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void Save(string directory, WindowPlacement value)
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "window.json"), temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, file, true);
    }
    public static Rect Fit(WindowPlacement value, Rect desktop, double minWidth, double minHeight)
    {
        double width = Math.Min(Math.Max(value.Width, minWidth), desktop.Width);
        double height = Math.Min(Math.Max(value.Height, minHeight), desktop.Height);
        return new Rect(Math.Clamp(value.Left, desktop.Left, Math.Max(desktop.Left, desktop.Right - width)),
            Math.Clamp(value.Top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - height)), width, height);
    }
}
