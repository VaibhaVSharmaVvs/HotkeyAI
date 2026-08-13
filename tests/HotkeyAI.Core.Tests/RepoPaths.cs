namespace HotkeyAI.Core.Tests;

/// <summary>
/// Locates repo files the tests read (examples, schema) by walking up from the test binary
/// until the solution file appears. Avoids baking build-output-relative paths into tests.
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Schema => Path.Combine(Root, "schema", "hotkeyai-dsl-v1.schema.json");

    public static string Examples => Path.Combine(Root, "examples");

    public static IEnumerable<string> ExampleFiles() =>
        Directory.EnumerateFiles(Examples, "*.json").OrderBy(f => f, StringComparer.Ordinal);

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
