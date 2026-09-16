using System.Text.Json;
using NetPaw.Model;
using NetPaw.Store;
using Xunit;

namespace NetPaw.Tests;

public class MainWindowTests
{
    [Fact]
    public void MainWindowStateRoundTrips()
    {
        var s = new Settings { MainWindow = new MainWindowState { X = 100, Y = 80, Width = 1100, Height = 700, Maximized = true, LastPage = "Tools" } };
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, JsonStore.Json), JsonStore.Json)!;
        Assert.Equal(100, back.MainWindow.X); Assert.Equal(700, back.MainWindow.Height); Assert.True(back.MainWindow.Maximized); Assert.Equal("Tools", back.MainWindow.LastPage);
        var fresh = JsonSerializer.Deserialize<Settings>("{}", JsonStore.Json)!;
        Assert.False(fresh.MainWindow.HasPosition); Assert.Equal(960, fresh.MainWindow.Width); Assert.Equal("Overview", fresh.MainWindow.LastPage);
    }

    [Fact]
    public void MainWindowStateClampsToScreen()
    {
        var screen = (0, 0, 1920, 1040);
        var c = new MainWindowState { X = 100, Y = 100, Width = 960, Height = 640 }.ClampTo(screen.Item1, screen.Item2, screen.Item3, screen.Item4);
        Assert.Equal((100, 100, 960, 640), (c.X, c.Y, c.Width, c.Height));                                  // fits: untouched
        var offscreen = new MainWindowState { X = 4000, Y = 100, Width = 960, Height = 640 }.ClampTo(0, 0, 1920, 1040);
        Assert.Equal(((1920 - 960) / 2, (1040 - 640) / 2), (offscreen.X, offscreen.Y));                    // monitor gone: centred
        var edge = new MainWindowState { X = 1800, Y = 1000, Width = 960, Height = 640 }.ClampTo(0, 0, 1920, 1040);
        Assert.True(edge.X <= 1920 - 200 && edge.Y <= 1040 - 150);                                          // partly visible: pulled in so 200×150 stay on screen
        var tiny = new MainWindowState { X = 10, Y = 10, Width = 100, Height = 50 }.ClampTo(0, 0, 1920, 1040);
        Assert.Equal((640, 400), (tiny.Width, tiny.Height));                                                // minimum size
        var huge = new MainWindowState { X = 0, Y = 0, Width = 5000, Height = 5000 }.ClampTo(0, 0, 1920, 1040);
        Assert.Equal((1920, 1040), (huge.Width, huge.Height));
        var never = new MainWindowState().ClampTo(100, 50, 1280, 720);                                      // never positioned: centred on the given screen
        Assert.Equal((100 + (1280 - 960) / 2, 50 + (720 - 640) / 2), (never.X, never.Y));
    }

    [Fact]
    public void TrayClickDefaultsToPanel() => Assert.Equal("panel", new Settings().TrayClick);
}
