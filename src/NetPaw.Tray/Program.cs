using System.Diagnostics;
using System.Security.Principal;
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

        // Guard: an elevated launch can run as a different account than the user at the screen
        // (e.g. "install with admin UAC" typed another admin's credentials). In that state per-user
        // state would be written for the wrong person, so surface it once and refuse autostart.
        var mismatch = OperatingSystem.IsWindows() ? UserContextWarning() : null;
        if (mismatch is not null) MessageBox.Show(mismatch, "NetPaw — user account", MessageBoxButtons.OK, MessageBoxIcon.Warning);

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
        try { app = new TrayApp(svc, showPanelAtStart: args.Contains("--panel"), currentUserMismatch: mismatch); svc.Store.Log("startup", $"tray ready after {startup.ElapsedMilliseconds} ms"); if (args.Contains("--main")) app.ShowMain(); }
        catch (Exception ex)
        {
            svc.Store.Log("startup failed: " + ex);
            MessageBox.Show(ex.ToString(), "NetPaw failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(app);
    }

    /// <summary>The cleartext of what this process is vs. the interactive session user, or null when they match/are unknown.</summary>
    static string? UserContextWarning()
    {
        string? effective = null;
        try { effective = WindowsIdentity.GetCurrent().Name; } catch (Exception) { }
        return NetPaw.UserContextGuard.MismatchWarning(effective, Native.InteractiveUserName());
    }
}
