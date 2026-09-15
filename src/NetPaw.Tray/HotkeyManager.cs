using NetPaw.Hotkeys;

namespace NetPaw.Tray;

/// <summary>Global hotkeys via RegisterHotKey on a message-only window; ids map to actions.</summary>
sealed class HotkeyManager : NativeWindow, IDisposable
{
    readonly Dictionary<int, (Hotkey Key, Action Action)> _map = [];
    int _next = 1;
    public event Action? ShowRequested;

    public HotkeyManager() => CreateHandle(new CreateParams { Caption = "NetPaw.Hotkeys" });

    /// <summary>Returns null on success or the reason (most often: another program already owns the combination).</summary>
    public string? Register(Hotkey key, Action action)
    {
        var id = _next++;
        if (!Native.RegisterHotKey(Handle, id, (uint)key.Modifiers | 0x4000 /* MOD_NOREPEAT */, (uint)key.VirtualKey))
            return $"{key} is already taken by another program";
        _map[id] = (key, action);
        return null;
    }

    public void UnregisterAll()
    {
        foreach (var id in _map.Keys) Native.UnregisterHotKey(Handle, id);
        _map.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _map.TryGetValue((int)m.WParam, out var e)) { e.Action(); return; }
        if (m.Msg == Native.WM_NETPAW_SHOW) { ShowRequested?.Invoke(); return; }
        base.WndProc(ref m);
    }

    public void Dispose() { UnregisterAll(); DestroyHandle(); }
}
