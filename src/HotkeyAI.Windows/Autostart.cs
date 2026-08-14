using Microsoft.Win32;

namespace HotkeyAI.Windows;

/// <summary>
/// Starting the agent at login, through the per-user Run key.
/// </summary>
/// <remarks>
/// A scheduled task was the first choice, because it can carry a start delay. It does not work:
/// creating an ONLOGON task is refused with "Access is denied" for a non-elevated user on a
/// default Windows 11 install, with or without <c>/RU</c> and <c>/RL LIMITED</c>. Asking for
/// administrator rights was not an acceptable price — a tool whose whole premise is that the user
/// should not have to learn the Startup folder cannot then demand elevation, and an app that
/// registers global hotkeys and synthesises keystrokes is the last thing that should be teaching
/// people to click through UAC for it.
/// <para>
/// The Run key needs no elevation, is per-user, and is trivially reversible. It also shows up in
/// Task Manager's Startup tab, which is a real advantage here rather than a compromise: software
/// that reads every keystroke should be visible in the place people look for exactly that, and
/// removable without going near this application.
/// </para>
/// <para>
/// The delay is not missed. Run entries are processed after the shell is up, which is what the
/// delay existed to wait for.
/// </para>
/// </remarks>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name, and what the user sees in Task Manager's Startup tab.</summary>
    private const string ValueName = "Hotkey AI";

    /// <summary>Whether Hotkey AI is set to start at login.</summary>
    public static bool IsEnabled() => Current() is not null;

    /// <summary>The command currently registered to run at login, if any.</summary>
    public static string? Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
#pragma warning disable CA1031 // Reported as "not enabled" rather than thrown at the caller.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Register the running executable to start at login.</summary>
    /// <returns>Null on success, or the reason it failed.</returns>
    public static string? Enable()
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            return "Could not determine this executable's path.";
        }

        // The CLI and the agent are separate executables, and it is the agent that must start.
        var agent = Path.Combine(Path.GetDirectoryName(exe)!, "hotkeyai-agent.exe");

        if (!File.Exists(agent))
        {
            return $"Could not find the agent next to this executable ({agent}).";
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key is null)
            {
                return $@"Could not open HKCU\{RunKey}.";
            }

            // Quoted: the path will contain spaces wherever this ends up installed, and an
            // unquoted Run value is parsed at the first one.
            key.SetValue(ValueName, $"\"{agent}\"");
            return null;
        }
#pragma warning disable CA1031 // Surfaced to the user as text; nothing here is worth crashing for.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }
    }

    /// <summary>Stop starting at login. Succeeds whether or not it was enabled.</summary>
    /// <returns>Null on success, or the reason it failed.</returns>
    public static string? Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return null;
        }
#pragma warning disable CA1031 // Surfaced to the user as text.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }
    }
}
