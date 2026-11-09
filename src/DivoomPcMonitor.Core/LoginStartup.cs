using Microsoft.Win32;

namespace DivoomPcMonitor;

/// <summary>
/// Per-user sign-in startup. This writes HKCU Run and does not ask for administrator.
/// </summary>
public static class LoginStartup
{
    public const string ValueName = "DivoomPcMonitor";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string CommandLine(string executablePath)
    {
        return "\"" + executablePath + "\" --background";
    }

    public static bool IsEnabled(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && SameCommand(value, CommandLine(executablePath));
    }

    public static bool SameCommand(string? stored, string expected)
    {
        return !string.IsNullOrWhiteSpace(stored)
            && string.Equals(stored.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user Run key.");
        key.SetValue(ValueName, CommandLine(executablePath));
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
