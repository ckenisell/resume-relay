using Microsoft.Win32;

namespace ResumeRelay.Windows;

/// <summary>
/// "Start with Windows" via the per-user Run key (no administrator rights needed):
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Run  →  "Resume Relay" = "C:\…\ResumeRelay.exe" --minimized
/// When Windows starts it, the app opens straight to the tray so it syncs without putting a window in the way.
/// </summary>
internal sealed class WindowsAutostart : IHostIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Resume Relay";
    private const string FirstRunMarker = "windows-first-run.done";

    /// <summary>Raised (on any thread) when the setting changes, so the tray menu can follow the web page.</summary>
    public event Action<bool>? Changed;

    public string Edition => "Windows app";
    public bool SupportsAutostart => true;

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public bool GetAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        Changed?.Invoke(enabled);
    }

    /// <summary>Starting with Windows is on by default: switch it on the very first time the app runs.
    /// After that the user's choice stands, even if they turn it off.</summary>
    public void ApplyFirstRunDefault(string dataDir)
    {
        string marker = Path.Combine(dataDir, FirstRunMarker);
        if (File.Exists(marker)) return;
        try { SetAutostart(true); } catch { }
        try { File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); } catch { }
    }

    /// <summary>If the user moved ResumeRelay.exe, point the existing Startup entry at its new location.</summary>
    public void RepairPathIfMoved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command);
        }
        catch { }
    }
}
