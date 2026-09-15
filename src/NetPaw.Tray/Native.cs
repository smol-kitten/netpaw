using System.Runtime.InteropServices;

namespace NetPaw.Tray;

static partial class Native
{
    public const int WM_HOTKEY = 0x0312;
    public static readonly int WM_NETPAW_SHOW = RegisterWindowMessage("NetPaw.ShowPanel");
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegisterWindowMessage(string lpString);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr handle);
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
