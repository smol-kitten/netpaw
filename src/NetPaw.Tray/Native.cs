using System.Runtime.InteropServices;

namespace NetPaw.Tray;

static partial class Native
{
    public const int WM_HOTKEY = 0x0312;
    public static readonly int WM_NETPAW_SHOW = RegisterWindowMessage("NetPaw.ShowPanel");
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    // ---- WTS session/user context ------------------------------------------------------------
    const int WTS_CURRENT_SERVER_HANDLE = ~0;   // IntPtr.MaxValue-ish sentinel for "local server"
    const int WTSUserName = 5;
    [LibraryImport("kernel32.dll")]
    public static partial uint WTSGetActiveConsoleSessionId();
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    public static partial IntPtr WTSQuerySessionInformation(IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void LocalFree(IntPtr hMem);

    /// <summary>Account name of the user sat at the current console (interactive) session, or null when none can be read.</summary>
    public static string? InteractiveUserName()
    {
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return null;
            if (WTSQuerySessionInformation(new IntPtr(WTS_CURRENT_SERVER_HANDLE), session, WTSUserName, out var buf, out _) == IntPtr.Zero || buf == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(buf); }
            finally { LocalFree(buf); }
        }
        catch (Exception) { return null; }   // never block startup on a diagnostic
    }

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegisterWindowMessage(string lpString);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr handle);
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();
    /// <summary>"DarkMode_Explorer" gives a ListView/TreeView dark scrollbars on Windows 10 1809+ (documented API, documented theme name).</summary>
    [LibraryImport("uxtheme.dll", EntryPoint = "SetWindowTheme", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Windows only lets the foreground process (or a hotkey handler) steal focus. A tray click or a
    /// second "NetPaw.exe" launch is neither, so briefly attach to the foreground thread's input queue.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(fgThread, me, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(fgThread, me, false); }
    }
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    /// <summary>Dark title bar + rounded corners on Windows 11; harmless elsewhere.</summary>
    public static void Dress(Form f, bool round = false)
    {
        try
        {
            var dark = 1; DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            if (round) { var pref = DWMWCP_ROUND; DwmSetWindowAttribute(f.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
