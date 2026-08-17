using System.Text.Json;
using HotkeyAI.Core.Dsl;
using HotkeyAI.Core.Json;
using HotkeyAI.Engine.Execution;

namespace HotkeyAI.Engine.Tests;

/// <summary>
/// The run-time half of the <c>open_path</c> extension policy — the half that matters.
/// </summary>
/// <remarks>
/// The validator can only judge a literal path, and the amplifying shape goes through a variable:
/// <c>list_files</c> over <c>Downloads</c> with pattern <c>*</c>, then <c>foreach</c> →
/// <c>open_path ${f.fullPath}</c>. That plan validates, and must — refusing the shape outright
/// would forbid every legitimate "open what I picked". So the refusal has to happen per resolved
/// value, here.
/// </remarks>
public sealed class ShellOpenExecutionTests
{
    private static readonly string[] Roots = [@"C:\Users\test\Downloads"];

    private static Automation Plan(string actions, string variables = "") =>
        JsonSerializer.Deserialize<Automation>(
            $$"""
            {
              "schemaVersion": 1,
              "name": "T",
              "trigger": { "type": "hotkey", "keys": ["CTRL","ALT","P"] },
              "variables": [{{variables}}],
              "actions": [{{actions}}]
            }
            """,
            DslJson.Options)!;

    private static PlanExecutor Executor(FakeDesktop desktop) =>
        new(desktop, new PathGuard(Roots));

    [Fact]
    public async Task TheReviewsAmplifyingShapeOpensTheDocumentsAndRefusesTheExecutable()
    {
        // The amplifying shape itself, against a Downloads folder holding what a browser might
        // have put there. The point is that the plan does not simply fail: it opens what it should
        // and refuses what it should, with the refusal named in the log.
        var desktop = new FakeDesktop();
        desktop.Directories[@"C:\Users\test\Downloads"] =
        [
            @"C:\Users\test\Downloads\invoice.pdf",
            @"C:\Users\test\Downloads\setup.exe",
            @"C:\Users\test\Downloads\notes.txt",
        ];

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "list_files", "id": "a1", "path": "C:\\Users\\test\\Downloads",
                   "pattern": "*", "into": "files" },
                 { "type": "foreach", "id": "a2", "source": "files", "itemVariable": "f",
                   "body": [
                     { "type": "open_path", "id": "a3", "path": "${f.fullPath}",
                       "onError": "continue" } ] }
                 """,
                 """
                 { "name": "files", "type": "pathList" },
                 { "name": "f", "type": "path" }
                 """),
            CancellationToken.None);

        Assert.Contains(desktop.Effects, e => e.EndsWith("invoice.pdf", StringComparison.Ordinal));
        Assert.Contains(desktop.Effects, e => e.EndsWith("notes.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(desktop.Effects, e => e.EndsWith("setup.exe", StringComparison.Ordinal));

        Assert.Contains(
            result.Entries,
            e => e.Detail.Contains("Windows executes rather than opens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExecutableReachedThroughAVariableIsRefused()
    {
        var desktop = new FakeDesktop { ClipboardText = @"C:\Users\test\Downloads\payload.bat" };

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "get_clipboard", "id": "a1", "into": "grabbed" },
                 { "type": "open_path", "id": "a2", "path": "${grabbed}" }
                 """,
                 """{ "name": "grabbed", "type": "text" }"""),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(desktop.Effects, e => e.StartsWith("open:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOrdinaryDocumentStillOpens()
    {
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "open_path", "id": "a1",
                   "path": "C:\\Users\\test\\Downloads\\invoice.pdf" }
                 """),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
    }

    [Fact]
    public async Task AFolderStillOpens()
    {
        // Nearly every open_path in the corpus opens a folder, which has no extension at all.
        var desktop = new FakeDesktop();

        var result = await Executor(desktop).RunAsync(
            Plan("""
                 { "type": "open_path", "id": "a1", "path": "C:\\Users\\test\\Downloads" }
                 """),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.ToTranscript());
    }
}
