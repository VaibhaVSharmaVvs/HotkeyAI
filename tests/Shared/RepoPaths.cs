namespace HotkeyAI.Tests;

/// <summary>
/// Locates repo files the tests read — the schema, the examples, the regression corpus — by
/// walking up from the test binary until the solution file appears.
/// </summary>
/// <remarks>
/// Shared by both test projects by linked compilation rather than copied, because two versions of
/// "where is the repo root" is exactly the sort of thing that works until someone changes the
/// output path of one project.
/// </remarks>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Schema => Path.Combine(Root, "schema", "hotkeyai-dsl-v1.schema.json");

    public static string Examples => Path.Combine(Root, "examples");

    /// <summary>The regression corpus: golden plans that must survive every DSL change.</summary>
    public static string CorpusPlans => Path.Combine(Root, "tests", "corpus", "plans");

    /// <summary>Rendered previews for the corpus, one per plan.</summary>
    public static string CorpusRendered => Path.Combine(Root, "tests", "corpus", "rendered");

    public static IEnumerable<string> ExampleFiles() =>
        Directory.EnumerateFiles(Examples, "*.json").OrderBy(f => f, StringComparer.Ordinal);

    public static IEnumerable<string> CorpusFiles() =>
        Directory.EnumerateFiles(CorpusPlans, "*.json").OrderBy(f => f, StringComparer.Ordinal);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("HotkeyAI.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repo root above {AppContext.BaseDirectory}.");
    }
}
