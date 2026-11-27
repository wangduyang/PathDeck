using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using QuickFileDesk;

internal static class FileSearchChecks
{
    internal static void Run()
    {
        var root = TestWorkspace.NewDirectory("files");
        try
        {
            RecycleChecks(root);
            PasteChecks(root);
            DirectoryWatchChecks(root);
            DirectoryWindowChecks(root);
            ArchiveChecks(root);
            SearchFallbackChecks().GetAwaiter().GetResult();
            Console.WriteLine("PASS: deletion safety flags/cancellation/results; copy/move collisions/partial failures; directory events and dispatcher-level root rename/recovery; archive roundtrip/collisions/in-flight cancellation/traversal cleanup; search fallback/pagination.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void RecycleChecks(string root)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "delete-folder")).FullName;
        var first = Path.Combine(root, "delete-file.txt");
        var failure = Path.Combine(root, "failed-file.txt");
        var pending = Path.Combine(root, "pending-file.txt");
        var untouched = Path.Combine(root, "untouched-file.txt");
        foreach (var path in new[] { first, failure, pending, untouched }) File.WriteAllText(path, "fixture");
        var order = new List<int>();
        var requests = new List<(string Path, bool Directory, UIOption Dialogs, RecycleOption Recycle, UICancelOption Cancel)>();
        var result = FileOperations.Recycle([first, folder, failure, pending, untouched], order.Add,
            (path, directory, dialogs, recycle, cancel) =>
            {
                requests.Add((path, directory, dialogs, recycle, cancel));
                if (path == failure) throw new IOException("simulated access failure");
                if (path == pending) throw new OperationCanceledException();
            });
        Check(result.Completed == 2 && result.Canceled && result.Remaining.SequenceEqual([failure, pending, untouched]) && result.Errors.Length == 1,
            "Deletion cancellation must preserve completed count, prior failures and unprocessed items.");
        Check(order.SequenceEqual([0, 1, 2, 3]) && requests[0].Directory == false && requests[1].Directory,
            "Deletion progress must report each current item and route files/folders correctly.");
        Check(requests.All(request => request.Dialogs == UIOption.AllDialogs && request.Recycle == RecycleOption.SendToRecycleBin &&
            request.Cancel == UICancelOption.ThrowException), "Every deletion must allow Windows safety dialogs and propagate cancellation.");

        var flagsMethod = typeof(FileSystem).GetMethod("GetOperationFlags", BindingFlags.NonPublic | BindingFlags.Static)!;
        var internalUi = flagsMethod.GetParameters()[0].ParameterType;
        int Flags(UIOption dialogs) => Convert.ToInt32(flagsMethod.Invoke(null, [Enum.Parse(internalUi, dialogs.ToString())]));
        const int noConfirmation = 0x10;
        Check((Flags(UIOption.OnlyErrorDialogs) & noConfirmation) != 0 && (Flags(requests[0].Dialogs) & noConfirmation) == 0,
            "The actual VisualBasic runtime must keep FOF_NOCONFIRMATION clear for the chosen deletion UI option.");
        Check(File.Exists(first) && Directory.Exists(folder) && File.Exists(failure) && File.Exists(pending) && File.Exists(untouched),
            "Deletion checks must never invoke a destructive shell operation.");

        var skipped = false;
        var invalid = FileOperations.Recycle(["relative.txt"], _ => { }, (_, _, _, _, _) => skipped = true);
        Check(!skipped && invalid.Completed == 0 && invalid.Remaining.Length == 1 && invalid.Errors.Length == 1,
            "Invalid deletion paths must fail before reaching the shell.");
    }

    private static void DirectoryWatchChecks(string root)
    {
        var original = Directory.CreateDirectory(Path.Combine(root, "watched")).FullName;
        using var watch = new DirectoryWatch(original, false);
        var child = Path.Combine(original, "item.txt");
        File.WriteAllText(child, "one");
        WaitForChange(watch, change => change.Contains(child), "A created child must refresh the listing.");
        Drain(watch);
        File.AppendAllText(child, "two");
        WaitForChange(watch, change => change.Contains(child), "An externally changed child must refresh its metadata.");
        Drain(watch);
        File.Delete(child);
        WaitForChange(watch, change => change.Contains(child), "A deleted child must refresh the listing.");
        Drain(watch);

        var moved = Path.Combine(root, "watched-renamed");
        Directory.Move(original, moved);
        WaitForChange(watch, change => change.All, "Renaming the watched root must invalidate all cached entries.");
        Directory.CreateDirectory(original);
        WaitForChange(watch, change => change.All, "Recreating the original root must recover listing refreshes.");
        var restored = Path.Combine(original, "restored.txt");
        File.WriteAllText(restored, "restored");
        WaitForChange(watch, change => change.Contains(restored), "The replacement root must continue receiving refreshes.");
        Directory.Delete(original, true);
        WaitForChange(watch, change => change.All, "Deleting the watched root must invalidate cached entries.");

        static void Drain(DirectoryWatch watch)
        {
            Thread.Sleep(150);
            while (watch.TakeChanges() is not null) { }
        }
    }

    private static void PasteChecks(string root)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "paste-source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(root, "paste-target")).FullName;
        var copied = Path.Combine(source, "copy.txt");
        File.WriteAllText(copied, "source");
        File.WriteAllText(Path.Combine(destination, "copy.txt"), "existing");
        var missing = Path.Combine(source, "missing.txt");
        var copy = FileOperations.Paste([missing, copied], destination, move: false);
        Check(copy.Completed == 1 && copy.Remaining.SequenceEqual([missing]) && copy.Errors.Length == 1 &&
            File.ReadAllText(Path.Combine(destination, "copy.txt")) == "existing" &&
            File.ReadAllText(Path.Combine(destination, "copy - 副本.txt")) == "source" && File.Exists(copied),
            "Copy must retain the source and existing destination, allocate a collision name and continue after a missing item.");

        var moved = Path.Combine(source, "move.txt");
        var movedTarget = Path.Combine(destination, "move - 副本.txt");
        File.WriteAllText(moved, "move");
        File.WriteAllText(Path.Combine(destination, "move.txt"), "existing");
        var move = FileOperations.Paste([missing, moved], destination, move: true);
        Check(move.Completed == 1 && move.Remaining.SequenceEqual([missing]) && move.Errors.Length == 1 &&
            move.Moves is not null && move.Moves[moved] == movedTarget && !File.Exists(moved) &&
            File.ReadAllText(movedTarget) == "move" && File.ReadAllText(Path.Combine(destination, "move.txt")) == "existing",
            "Move must preserve collision targets and report only successful source-to-target mappings after partial failure.");

        var same = FileOperations.Paste([copied], source, move: true);
        Check(same.Completed == 0 && same.Remaining.SequenceEqual([copied]) && same.Errors.Length == 0 && File.Exists(copied),
            "Moving an item to its current directory must leave it available for a later paste.");
        var nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        var self = FileOperations.Paste([source], nested, move: false);
        Check(self.Completed == 0 && self.Remaining.SequenceEqual([source]) && self.Errors.Length == 1,
            "Copying a folder into itself must fail before a shell operation.");
        File.WriteAllText(Path.Combine(destination, "data.tar.gz"), "collision");
        Check(Path.GetFileName(FileOperations.AvailableName(destination, "data.tar.gz", false, ".tar.gz")) == "data - 副本.tar.gz",
            "Compound archive extensions must remain intact when allocating a collision name.");
    }

    private static void DirectoryWindowChecks(string root)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var original = Directory.CreateDirectory(Path.Combine(root, "window-watched")).FullName;
        var first = Path.Combine(original, "before.txt");
        File.WriteAllText(first, "before");
        var context = SynchronizationContext.Current;
        var app = Application.Current;
        var oldMain = app.MainWindow;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(original) { ShowInTaskbar = false };
        try
        {
            // Keep the window unshown so Start does not load saved locations or enumerate drives.
            typeof(MainWindow).GetMethod("AddTab", privateInstance)!.Invoke(window, [original]);
            var tabs = (TabControl)window.FindName("Tabs");
            var list = (ListView)window.FindName("FileList");
            var status = (TextBlock)window.FindName("StatusText");
            var tab = (BrowserTab)((TabItem)tabs.SelectedItem).Tag;
            Until(() => tab.Loaded && list.Items.Cast<FileEntry>().Any(entry => entry.FullPath == first),
                "The hidden file window must load the synthetic folder on its dispatcher.", refresh: false);

            Directory.Move(original, Path.Combine(root, "window-watched-renamed"));
            Until(() => list.Items.Count == 0 && tab.Entries.Length == 0 && status.Text.Contains("等待原路径恢复", StringComparison.Ordinal),
                "Renaming the viewed root must clear actual UI/cached entries and show the missing-folder status.");
            Check(tab.Path == original, "Root disappearance must retain the original path for recovery.");

            Directory.CreateDirectory(original);
            var restored = Path.Combine(original, "after.txt");
            File.WriteAllText(restored, "after");
            Until(() => list.Items.Cast<FileEntry>().Any(entry => entry.FullPath == restored) &&
                tab.Entries.Any(entry => entry.FullPath == restored) && status.Text == "文件夹已恢复。",
                "Recreating the viewed root must restore actual UI/cached entries and replace the missing-folder status.");
            Check(!window.IsVisible, "Directory recovery checks must leave the file window unshown.");
            Console.WriteLine("PASS: hidden MainWindow dispatcher clears renamed-root entries/status and restores the original path after recreation.");

            void Until(Func<bool> condition, string failure, bool refresh = true)
            {
                var until = Environment.TickCount64 + 8000;
                while (Environment.TickCount64 < until)
                {
                    if (condition()) return;
                    if (refresh)
                    {
                        var task = (Task)typeof(MainWindow).GetMethod("RefreshChangedDirectory", privateInstance)!.Invoke(window, null)!;
                        while (!task.IsCompleted && Environment.TickCount64 < until) PumpDispatcher();
                        Check(task.IsCompleted, failure);
                        task.GetAwaiter().GetResult();
                    }
                    PumpDispatcher();
                }
                throw new Exception(failure);
            }
        }
        finally
        {
            window.Close();
            app.MainWindow = oldMain;
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void ArchiveChecks(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "archives")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(directory, "bundle")).FullName;
        var note = Path.Combine(folder, "notes.txt");
        File.WriteAllText(note, "archive fixture");
        File.WriteAllText(Path.Combine(directory, "bundle.zip"), "keep existing archive");
        var archive = ArchiveFiles.Compress(directory, [folder, note], "bundle.zip", ".zip", CancellationToken.None, null);
        Check(Path.GetFileName(archive) == "bundle - 副本.zip" && File.ReadAllText(Path.Combine(directory, "bundle.zip")) == "keep existing archive",
            "Archive creation must allocate a collision name without replacing existing data.");
        using (var zip = ZipFile.OpenRead(archive))
            Check(zip.Entries.Count(entry => entry.FullName.EndsWith("notes.txt", StringComparison.Ordinal)) == 1,
                "Selecting a folder and its child must include the child only once.");
        var existingOutput = Directory.CreateDirectory(Path.Combine(directory, "bundle - 副本")).FullName;
        var marker = Path.Combine(existingOutput, "keep.txt");
        File.WriteAllText(marker, "keep");
        var extracted = ArchiveFiles.Extract(archive, CancellationToken.None, null);
        Check(extracted != existingOutput && File.ReadAllText(marker) == "keep" &&
            File.ReadAllText(Path.Combine(extracted, "bundle", "notes.txt")) == "archive fixture",
            "Extraction must preserve existing folders and roundtrip the selected content into a new collision folder.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            ArchiveFiles.Compress(directory, [note], "canceled.zip", ".zip", canceled.Token, null);
            throw new Exception("A pre-canceled archive operation must throw.");
        }
        catch (OperationCanceledException) { }
        Check(!File.Exists(Path.Combine(directory, "canceled.zip")), "Canceled compression must not publish an output archive.");

        var beforeCancellation = Directory.GetDirectories(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var inFlight = new CancellationTokenSource();
        var started = false;
        var stagingObserved = false;
        var cancelProgress = new InlineArchiveProgress(progress =>
        {
            if (progress.StartedEntries is not > 0 || inFlight.IsCancellationRequested) return;
            started = true;
            stagingObserved = Directory.EnumerateDirectories(directory).Any(ArchiveFiles.IsStagingPath);
            inFlight.Cancel();
        });
        try
        {
            ArchiveFiles.Extract(archive, inFlight.Token, cancelProgress);
            throw new Exception("Canceling after tar reports an extracted entry must abort the active archive operation.");
        }
        catch (OperationCanceledException) { }
        Check(started && stagingObserved && inFlight.IsCancellationRequested &&
            beforeCancellation.SetEquals(Directory.GetDirectories(directory)),
            "In-flight extraction cancellation must occur after work/staging starts, remove staging and publish no final folder.");
        Console.WriteLine("PASS: tar extraction canceled after its first entry with live staging; no partial/final folder remains.");

        var unsafeArchive = Path.Combine(directory, "unsafe.zip");
        using (var zip = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
        using (var text = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) text.Write("must stay staged");
        try
        {
            ArchiveFiles.Extract(unsafeArchive, CancellationToken.None, null);
            throw new Exception("Traversal archive entries must be rejected.");
        }
        catch (IOException) { }
        Check(!File.Exists(Path.Combine(directory, "escaped.txt")) && !Directory.Exists(Path.Combine(directory, "unsafe")),
            "A rejected traversal archive must not escape staging or publish a partial output directory.");
        Check(!Directory.EnumerateFileSystemEntries(directory).Any(ArchiveFiles.IsStagingPath),
            "Successful, canceled and rejected archive operations must clean staging directories.");
    }

    private sealed class InlineArchiveProgress(Action<ArchiveProgress> report) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value) => report(value);
    }

    private static void WaitForChange(DirectoryWatch watch, Func<DirectoryChanges, bool> matches, string failure)
    {
        var until = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < until)
        {
            if (watch.TakeChanges() is { } change && matches(change)) return;
            Thread.Sleep(30);
        }
        throw new Exception(failure);
    }

    private static async Task SearchFallbackChecks()
    {
        foreach (var error in new Exception[] { new IOException("engine is still running"), new TimeoutException() })
        {
            var fallbackCount = 0;
            var requestedOffset = -1;
            var hits = new List<IndexedHit> { new("old", @"D:\fixture", false, 1, null) };
            var useEverything = true;
            var offset = 200;
            var expected = new IndexedPage(1, [new("windows", @"D:\fixture", false, 2, null)], null);
            var result = await SearchFallback.Query(true, offset,
                (_, _) => Task.FromException<IndexedPage>(error),
                (nextOffset, _) =>
                {
                    Check(!useEverything && hits.Count == 0 && offset == 0,
                        "Fallback must update the engine and clear stale hits/pagination before querying Windows Search.");
                    requestedOffset = nextOffset;
                    return Task.FromResult(expected);
                },
                () => { fallbackCount++; useEverything = false; hits.Clear(); offset = 0; }, CancellationToken.None);
            Check(result == expected && requestedOffset == 0 && fallbackCount == 1,
                "A recoverable Everything failure must query Windows Search once at offset zero, even while Everything remains running.");
        }

        using var canceled = new CancellationTokenSource();
        var fellBack = false;
        var windowsCalled = false;
        try
        {
            await SearchFallback.Query(true, 200, (_, token) =>
            {
                canceled.Cancel();
                return Task.FromException<IndexedPage>(new IOException("canceled send"));
            }, (_, _) => { windowsCalled = true; return Task.FromResult(new IndexedPage(null, [], null)); },
                () => fellBack = true, canceled.Token);
            throw new Exception("A canceled recoverable error must remain canceled without fallback.");
        }
        catch (IOException) { }
        Check(!fellBack && !windowsCalled, "Cancellation must not switch the search engine or issue a fallback query.");

        try
        {
            await SearchFallback.Query(true, 0, (_, _) => Task.FromException<IndexedPage>(new OperationCanceledException()),
                (_, _) => { windowsCalled = true; return Task.FromResult(new IndexedPage(null, [], null)); },
                () => fellBack = true, CancellationToken.None);
            throw new Exception("OperationCanceledException must propagate without fallback.");
        }
        catch (OperationCanceledException) { }
        Check(!fellBack && !windowsCalled, "A canceled engine request must not fall back.");

        try
        {
            await SearchFallback.Query(true, 0, (_, _) => Task.FromException<IndexedPage>(new InvalidOperationException("unexpected failure")),
                (_, _) => { windowsCalled = true; return Task.FromResult(new IndexedPage(null, [], null)); },
                () => fellBack = true, CancellationToken.None);
            throw new Exception("Nonrecoverable errors must propagate without fallback.");
        }
        catch (InvalidOperationException) { }
        Check(!fellBack && !windowsCalled, "Unexpected engine failures must not silently change engines.");

        var appendedOffset = -1;
        await SearchFallback.Query(false, 200,
            (_, _) => throw new Exception("A Windows Search continuation must not retry Everything."),
            (nextOffset, _) => { appendedOffset = nextOffset; return Task.FromResult(new IndexedPage(null, [], null)); },
            () => throw new Exception("A Windows Search continuation is already using the fallback engine."), CancellationToken.None);
        Check(appendedOffset == 200, "Windows Search continuation must retain its own pagination offset.");
    }
}
