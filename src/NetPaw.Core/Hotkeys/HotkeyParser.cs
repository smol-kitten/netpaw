namespace NetPaw.Hotkeys;

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// <summary>A parsed hotkey: Win32 MOD_* flags plus a virtual-key code, ready for RegisterHotKey.</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey, string KeyName)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName);
        return string.Join("+", parts);
    }
}

public static class HotkeyParser
{
    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["PrintScreen"] = 0x2C,
        ["Plus"] = 0xBB, ["Minus"] = 0xBD, ["Comma"] = 0xBC, ["Period"] = 0xBE,
        ["NumpadMultiply"] = 0x6A, ["NumpadAdd"] = 0x6B, ["NumpadSubtract"] = 0x6D, ["NumpadDivide"] = 0x6F,
    };

    /// <summary>Parses "Ctrl+Alt+1", "Win+Shift+F5", "Ctrl+Numpad3". Requires at least one modifier — a bare key would steal normal typing.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win" or "windows" or "super" or "meta": mods |= HotkeyModifiers.Win; break;
                default: if (key is not null) return false; key = raw; break;
            }
        }
        if (key is null || mods == HotkeyModifiers.None) return false;
        if (!TryKey(key, out var vk, out var name)) return false;
        hotkey = new Hotkey(mods, vk, name);
        return true;
    }

    static bool TryKey(string key, out int vk, out string name)
    {
        vk = 0; name = key;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) { name = key.ToUpperInvariant(); vk = name[0]; return true; }
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) { name = "F" + f; vk = 0x70 + f - 1; return true; }
        if (key.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsAsciiDigit(key[6])) { name = "Numpad" + key[6]; vk = 0x60 + (key[6] - '0'); return true; }
        if (Named.TryGetValue(key, out vk)) { name = Named.Keys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)); return true; }
        return false;
    }
}
