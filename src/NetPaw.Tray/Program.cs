using System.Diagnostics;
using NetPaw.Store;

namespace NetPaw.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var startup = System.Diagnostics.Stopwatch.StartNew();
        using var mutex = new Mutex(true, @"Local\NetPaw.Tray", out var first);
        if (!first)
        {
            // Second launch = "show the panel" (also what a pinned shortcut does).
            Native.PostMessage(Native.HWND_BROADCAST, Native.WM_NETPAW_SHOW, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        NetPawService svc;
        try { svc = NetPawService.CreateDefault(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "NetPaw failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        TelemetryHost.Init(svc);
        Application.ThreadException += (_, e) => { svc.Store.Log("unhandled: " + e.Exception); TelemetryHost.Error(e.Exception, "ui"); MessageBox.Show(e.Exception.Message, "NetPaw", MessageBoxButtons.OK, MessageBoxIcon.Error); };
        TrayApp app;
        try { app = new TrayApp(svc, showPanelAtStart: args.Contains("--panel")); svc.Store.Log("startup", $"tray ready after {startup.ElapsedMilliseconds} ms"); if (args.Contains("--main")) app.ShowMain(); }
        catch (Exception ex)
        {
            svc.Store.Log("startup failed: " + ex);
            MessageBox.Show(ex.ToString(), "NetPaw failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(app);
    }
}
