using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Windows;

/// <summary>
/// The Win32 implementation of <see cref="IDesktop"/>.
/// </summary>
/// <remarks>
/// This is the only place in the product where the engine meets the operating system. Everything
/// above it — the DSL, both validators, the executor and every safety control — is plain
/// <c>net10.0</c> and covered by tests that run on Linux; this project is where that stops being
/// true, which is the reason it is small and does no decision-making of its own.
/// </remarks>
public sealed class WindowsDesktop : IDesktop
{
    public WindowsDesktop(IPrompts? prompts = null, AppResolver? resolver = null)
    {
        Resolver = resolver ?? new AppResolver();
        Processes = new WindowsProcesses(Resolver);
        Windows = new WindowsWindows();
        Input = new WindowsInput();
        Files = new WindowsFiles();
        Clipboard = new WindowsClipboard();
        Prompts = prompts ?? new ConsolePrompts();
    }

    /// <summary>Exposed for diagnostics — `hotkeyai apps` reports what resolves.</summary>
    public AppResolver Resolver { get; }

    public IProcesses Processes { get; }

    public IWindows Windows { get; }

    public IInput Input { get; }

    public IFiles Files { get; }

    public IClipboard Clipboard { get; }

    public IPrompts Prompts { get; }
}
