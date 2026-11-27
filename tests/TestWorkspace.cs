using System.IO;

internal static class TestWorkspace
{
    internal static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("PATHDECK_TEST_ROOT")
        ?? @"D:\codex\.tmp\pathdeck-checks");
    internal static string NewDirectory(string name)
    {
        string path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
