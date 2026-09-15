using System.Diagnostics;
using NetPaw.Store;

namespace NetPaw.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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
        Application.ThreadException += (_, e) => { svc.Store.Log("unhandled: " + e.Exception); MessageBox.Show(e.Exception.Message, "NetPaw", MessageBoxButtons.OK, MessageBoxIcon.Error); };
        Application.Run(new TrayApp(svc, showPanelAtStart: args.Contains("--panel")));
    }
}
