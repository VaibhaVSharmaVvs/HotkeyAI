using HotkeyAI.Core.Dsl;
using HotkeyAI.Engine.Platform;

namespace HotkeyAI.Windows;

/// <summary>
/// Console implementations of the user-facing prompts.
/// </summary>
/// <remarks>
/// A stand-in so automations can be run and tested from the CLI before the WPF picker overlay
/// exists. The real <c>show_picker</c> is a fuzzy-search overlay with keyboard navigation and
/// correct focus restore — a genuine component, not a primitive — and it arrives with the UI.
/// Keeping the seam here means the engine never learns which one it is talking to.
/// </remarks>
public sealed class ConsolePrompts : IPrompts
{
    public ValueTask<string?> PickAsync(
        IReadOnlyList<string> items, string? prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        Console.WriteLine();
        Console.WriteLine(prompt ?? "Choose one:");

        for (var i = 0; i < items.Count; i++)
        {
            Console.WriteLine($"  {i + 1,3}. {items[i]}");
        }

        Console.Write("Number (blank to cancel): ");
        var answer = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(answer))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult(
            int.TryParse(answer, out var choice) && choice >= 1 && choice <= items.Count
                ? items[choice - 1]
                : null);
    }

    public ValueTask<string?> AskAsync(
        string prompt, string? defaultValue, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.Write(string.IsNullOrEmpty(defaultValue)
            ? $"{prompt} "
            : $"{prompt} [{defaultValue}] ");

        var answer = Console.ReadLine();

        if (answer is null)
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(
            answer.Length == 0 ? defaultValue ?? "" : answer);
    }

    public ValueTask NotifyAsync(
        string message, NotifyLevel level, CancellationToken cancellationToken)
    {
        var marker = level switch
        {
            NotifyLevel.Warning => "warning",
            NotifyLevel.Error => "error",
            _ => "info",
        };

        Console.WriteLine($"  [{marker}] {message}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.Write($"{message} [y/N] ");
        var answer = Console.ReadLine();

        // Defaulting to no is deliberate: this gate exists for destructive actions, and someone
        // hitting Enter to get past a prompt should not thereby kill a process.
        return ValueTask.FromResult(
            answer is not null && answer.Trim().StartsWith('y'));
    }
}
