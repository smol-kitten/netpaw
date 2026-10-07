using Microsoft.Win32;

namespace NetPaw;

/// <summary>Windows <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> store for <see cref="Autorun"/>.
/// HKCU is the current (interactive) user, so the value — and therefore the logon autostart — belongs to the
/// person at the screen, never an admin who elevated an installer.</summary>
public sealed class RegistryAutorunStore : IAutorunStore
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool Exists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(Autorun.RunValueName) is string s && s.Length > 0;
    }

    public void Set(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath) ?? throw new InvalidOperationException("cannot open HKCU Run key");
        key.SetValue(Autorun.RunValueName, command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key?.GetValue(Autorun.RunValueName) is not null) key.DeleteValue(Autorun.RunValueName, throwOnMissingValue: false);
    }
}
