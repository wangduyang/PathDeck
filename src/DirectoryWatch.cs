using System.IO;
using System.Security;

namespace QuickFileDesk;

internal readonly record struct DirectoryChanges(HashSet<string> Paths, bool All)
{
    public bool Contains(string path) => All || Paths.Contains(path);
}

internal sealed class DirectoryWatch : IDisposable
{
    private readonly object gate = new();
    private readonly FileSystemWatcher? watcher;
    private HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private bool all;
    private bool polling;
    private long nextPoll;
    private long retryAt;

    public DirectoryWatch(string path, bool recursive)
    {
        Path = path;
        Recursive = recursive;
        FileSystemWatcher? candidate = null;
        try
        {
            candidate = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                    NotifyFilters.Size | NotifyFilters.Attributes
            };
            candidate.Created += (_, change) => Mark(change.FullPath);
            candidate.Changed += (_, change) => Mark(change.FullPath);
            candidate.Deleted += (_, change) => Mark(change.FullPath);
            candidate.Renamed += (_, change) =>
            {
                Mark(change.OldFullPath);
                Mark(change.FullPath);
            };
            candidate.Error += (_, error) =>
            {
                lock (gate)
                {
                    all = true;
                    if (error.GetException() is not InternalBufferOverflowException) polling = true;
                }
            };
            candidate.EnableRaisingEvents = true;
            watcher = candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            candidate?.Dispose();
            polling = true;
        }
    }

    public string Path { get; }
    public bool Recursive { get; }

    public bool Matches(string path, bool recursive) => Recursive == recursive && FileOperations.SamePath(Path, path);

    public DirectoryChanges? TakeChanges()
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            if (now < retryAt) return null;
            if (retryAt != 0)
            {
                all = true;
                retryAt = 0;
            }

            if (paths.Count == 0 && !all && (!polling || now < nextPoll)) return null;

            var changes = new DirectoryChanges(paths, all);
            paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            all = false;
            nextPoll = now + 2000;
            return changes;
        }
    }

    public void RetryLater()
    {
        lock (gate) retryAt = Environment.TickCount64 + 2000;
    }

    private void Mark(string path)
    {
        if (ArchiveFiles.IsStagingPath(path)) return;
        lock (gate)
        {
            if (all) return;
            paths.Add(path);
            if (paths.Count <= 512) return;
            all = true;
            paths.Clear();
        }
    }

    public void Dispose() => watcher?.Dispose();
}
