using HotkeyAI.Engine.Platform;
using Microsoft.Win32;

namespace HotkeyAI.Windows;

/// <summary>
/// Turns a logical application name into an executable on this machine.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <c>"app": "vscode"</c> worth preferring over a hard-coded path: the plan
/// says what it wants, and the machine decides where that lives. A plan written on one machine
/// keeps working on another, and survives the application moving itself — which installers that
/// live under <c>%LocalAppData%</c> do on every update.
/// </para>
/// <para>
/// Resolution order matters. The <c>App Paths</c> registry key is the answer Windows itself uses
/// when you type a name into Run, so it reflects what the user actually installed. Explicit
/// candidate locations come next, for applications that do not register there. <c>PATH</c> is
/// last because it is the least trustworthy: anything can put an executable on it.
/// </para>
/// </remarks>
public sealed class AppResolver
{
    private const string AppPathsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

    /// <summary>Candidate executables per logical name, in preference order.</summary>
    /// <remarks>
    /// Several entries list more than one: Outlook and PowerShell each ship in two generations,
    /// and picking the wrong one silently launches something the user did not mean.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Candidates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["vscode"] = ["Code.exe"],
            ["explorer"] = ["explorer.exe"],
            ["chrome"] = ["chrome.exe"],
            ["edge"] = ["msedge.exe"],
            ["firefox"] = ["firefox.exe"],
            ["terminal"] = ["wt.exe", "WindowsTerminal.exe"],
            ["powershell"] = ["pwsh.exe", "powershell.exe"],
            ["notepad"] = ["notepad.exe"],
            ["spotify"] = ["Spotify.exe"],
            ["slack"] = ["slack.exe"],
            // The current Teams is the Store build: process ms-teams, not the old Teams.exe.
            // Both are listed because the classic client is still on plenty of machines.
            ["teams"] = ["ms-teams.exe", "Teams.exe"],
            ["discord"] = ["Discord.exe", "Update.exe"],
            ["cursor"] = ["Cursor.exe"],
            ["outlook"] = ["olk.exe", "OUTLOOK.EXE"],
            ["obsidian"] = ["Obsidian.exe"],
        };

    /// <summary>Directories to probe when the registry does not know.</summary>
    private static readonly Dictionary<string, string[]> KnownLocations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["vscode"] = [@"%LOCALAPPDATA%\Programs\Microsoft VS Code", @"%ProgramFiles%\Microsoft VS Code"],
            ["explorer"] = [@"%WINDIR%"],
            ["notepad"] = [@"%WINDIR%\System32", @"%WINDIR%"],
            ["powershell"] = [@"%ProgramFiles%\PowerShell\7", @"%WINDIR%\System32\WindowsPowerShell\v1.0"],
            ["terminal"] = [@"%LOCALAPPDATA%\Microsoft\WindowsApps"],
            ["spotify"] = [@"%APPDATA%\Spotify", @"%LOCALAPPDATA%\Microsoft\WindowsApps"],
            ["slack"] = [@"%LOCALAPPDATA%\slack"],
            ["teams"] = [@"%LOCALAPPDATA%\Microsoft\WindowsApps", @"%LOCALAPPDATA%\Microsoft\Teams\current"],
            ["discord"] = [@"%LOCALAPPDATA%\Discord"],
            ["cursor"] = [@"%LOCALAPPDATA%\Programs\cursor", @"%ProgramFiles%\cursor"],
            ["obsidian"] = [@"%LOCALAPPDATA%\Obsidian"],
            ["outlook"] = [@"%LOCALAPPDATA%\Microsoft\WindowsApps", @"%ProgramFiles%\Microsoft Office\root\Office16"],
            ["chrome"] = [@"%ProgramFiles%\Google\Chrome\Application", @"%ProgramFiles(x86)%\Google\Chrome\Application"],
            ["edge"] = [@"%ProgramFiles(x86)%\Microsoft\Edge\Application", @"%ProgramFiles%\Microsoft\Edge\Application"],
            ["firefox"] = [@"%ProgramFiles%\Mozilla Firefox", @"%ProgramFiles(x86)%\Mozilla Firefox"],
        };

    private readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve a logical name, or null if the application is not installed.</summary>
    /// <remarks>
    /// Returning null rather than guessing is deliberate. The engine reports "not installed" as a
    /// failure the user can act on; a guess would launch the wrong thing and look like success.
    /// </remarks>
    public string? Resolve(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        lock (cache)
        {
            if (cache.TryGetValue(logicalName, out var cached))
            {
                return cached;
            }

            var resolved = Probe(logicalName);
            cache[logicalName] = resolved;
            return resolved;
        }
    }

    /// <summary>Every known name and where it resolved, for diagnostics.</summary>
    public IReadOnlyDictionary<string, string?> ResolveAll() =>
        Candidates.Keys.ToDictionary(name => name, Resolve, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where applications are allowed to live.
    /// </summary>
    /// <remarks>
    /// Resolution consults sources the user can write — HKCU's <c>App Paths</c> and <c>PATH</c> —
    /// so the answer has to be checked rather than trusted. These are the directories a real
    /// installation uses; anything resolving outside them is reported instead of launched.
    /// <para>
    /// <c>%LOCALAPPDATA%\Programs</c> is here because per-user installs are ordinary now — VS Code
    /// and Cursor both live there — which does mean a user-writable directory is trusted. That is a
    /// deliberate limit of this fix: it closes redirection through the registry and PATH, not
    /// tampering with an installed binary, which nothing at this layer can detect.
    /// </para>
    /// </remarks>
    private static readonly string[] TrustedRoots =
    [
        "%ProgramFiles%",
        "%ProgramFiles(x86)%",
        "%WINDIR%",
        @"%LOCALAPPDATA%\Programs",
        @"%LOCALAPPDATA%\Microsoft\WindowsApps",
        @"%ProgramData%\chocolatey",
    ];

    /// <summary>
    /// Resolve for launching, refusing an executable found somewhere it should not be.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Resolve"/>, which the dashboard and the validator use to answer
    /// "is this app installed" — a question where a binary in an odd place is still an answer.
    /// Launching is the operation that needs the stricter rule.
    /// </remarks>
    public AppResolution ResolveForLaunch(string logicalName)
    {
        if (Resolve(logicalName) is not { } resolved)
        {
            return AppResolution.None;
        }

        var full = Path.GetFullPath(resolved);

        foreach (var root in TrustedRoots)
        {
            var expanded = Environment.ExpandEnvironmentVariables(root);

            // Unexpanded variables come back verbatim, and %ProgramFiles(x86)% is absent on ARM.
            if (expanded.Contains('%', StringComparison.Ordinal))
            {
                continue;
            }

            if (full.StartsWith(
                    expanded.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return AppResolution.At(full);
            }
        }

        return AppResolution.Refused(
            $"it resolved to \"{full}\", which is not in a directory applications are installed "
            + "in. A logical app name is looked up through the registry and PATH, both of which "
            + "this account can write, so a resolved path outside the install directories is "
            + "treated as redirection rather than an installation.");
    }

    private static string? Probe(string logicalName)
    {
        if (!Candidates.TryGetValue(logicalName, out var executables))
        {
            return null;
        }

        foreach (var executable in executables)
        {
            if (FromAppPaths(executable) is { } registered)
            {
                return registered;
            }
        }

        if (KnownLocations.TryGetValue(logicalName, out var directories))
        {
            foreach (var directory in directories)
            {
                var expanded = Environment.ExpandEnvironmentVariables(directory);
                foreach (var executable in executables)
                {
                    var candidate = Path.Combine(expanded, executable);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        foreach (var executable in executables)
        {
            if (FromPath(executable) is { } onPath)
            {
                return onPath;
            }
        }

        return null;
    }

    /// <summary>
    /// The location Windows itself uses when a bare name is typed into Run.
    /// </summary>
    /// <remarks>
    /// Machine before user, and that order is load-bearing.
    /// It was the other way round, and HKCU is writable by any process running as the user — so
    /// malware could point <c>App Paths\notepad.exe</c> at its own binary and an automation
    /// approved months earlier, rendered as "Launch notepad", would launch it. This file already
    /// put PATH last on the grounds that "anything can put an executable on it"; HKCU deserves the
    /// same suspicion and was getting the opposite.
    /// </remarks>
    private static string? FromAppPaths(string executable)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(AppPathsKey + executable);
            if (key?.GetValue(null) is string value && value.Length > 0)
            {
                var path = value.Trim('"');
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string? FromPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is common and not worth failing over.
            }
        }

        return null;
    }
}
