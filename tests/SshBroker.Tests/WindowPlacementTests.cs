using System.Windows;
using SshBroker.Gui;
using Xunit;

namespace SshBroker.Tests;

public class WindowPlacementTests
{
    [Fact]
    public void WindowSizeAndMaximizedStateSurviveRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SshBroker-window-" + Guid.NewGuid().ToString("N"));
        var expected = new WindowPlacement(1420, 900, 120, 80, true);
        WindowPlacementStore.Save(directory, expected);
        Assert.Equal(expected, WindowPlacementStore.Load(directory));
        var normal = expected with { Width = 1210, Height = 810, Maximized = false };
        WindowPlacementStore.Save(directory, normal);
        Assert.Equal(normal, WindowPlacementStore.Load(directory));
    }
    [Fact]
    public void ChangedDesktopMovesSavedWindowIntoVisibleArea()
    {
        var saved = new WindowPlacement(2200, 1400, 3000, -2000, false);
        var desktop = new Rect(0, 0, 1920, 1080);
        Rect fitted = WindowPlacementStore.Fit(saved, desktop, 1120, 760);
        Assert.True(desktop.Contains(fitted));
        Assert.Equal(1920, fitted.Width); Assert.Equal(1080, fitted.Height);
    }
}
