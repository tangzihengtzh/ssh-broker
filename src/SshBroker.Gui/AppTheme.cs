using System.Windows;
using System.Windows.Media;
using ModernWpf;

namespace SshBroker.Gui;

public static class AppTheme
{
    public static void Apply(string palette)
    {
        palette = SshBroker.Core.ConnectionHistory.NormalizePalette(palette);
        ThemeManager.Current.ApplicationTheme = palette == "Dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;
        string[] colors = palette switch
        {
            "Dark" => ["#171B24", "#212735", "#2A3243", "#E7ECF5", "#A3AFC4", "#364156", "#879FFF", "#1B212D", "#263249"],
            "Warm" => ["#F5F0E8", "#FFFDF8", "#F1E9DD", "#40382E", "#837566", "#E5DACB", "#946538", "#FAF6EF", "#EDE0CB"],
            "Blue" => ["#EAF2FB", "#F9FCFF", "#E3EFFB", "#203A55", "#627F9B", "#D1E1F0", "#3477B9", "#F0F7FE", "#D8E9F9"],
            _ => ["#F5F7FB", "#FFFFFF", "#EEF2FA", "#202B40", "#6B778D", "#E5EAF2", "#4263EB", "#F8FAFD", "#EAF0F9"]
        };
        string[] keys = ["CanvasBrush", "CardBrush", "SoftBrush", "InkBrush", "MutedBrush", "LineBrush", "AccentBrush", "FieldBrush", "TabHoverBrush"];
        ThemeManager.Current.AccentColor = (Color)ColorConverter.ConvertFromString(colors[6]);
        for (int index = 0; index < keys.Length; index++)
            Application.Current.Resources[keys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[index]));
    }
}
