using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickFileDesk;

internal static class ImageReviewChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string fixture = "";

    internal static void Run()
    {
        fixture = TestWorkspace.NewDirectory("images");
        var failures = new List<string>();
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            Case("NIfTI normalized units and spatial corners", SpatialAlignment, failures);
            Case("NIfTI singleton axes compare voxel footprints", SingletonAlignment, failures);
            Case("source overwrite retains original pixels, marks and export", SourceRetention, failures);
            Case("current source overwrite is checked when navigating away", DepartingSource, failures);
            Case("successful save and modal source/navigation guards", SaveWorkflow, failures);
            Case("same dimensions sync and mismatch pause", ComparisonSync, failures);
            Case("canceled and superseded loads cannot export", CanceledLoads, failures);
            Case("hash cancellation after reading and concurrent writer exclusion", SourceReadSafety, failures);
            Case("oversized raster sources reject before hashing", RasterInputLimits, failures);
            Case("navigation releases unedited snapshots and retains saved work", SnapshotRetention, failures);
            Case("undoing all saved marks remains unsaved", SavedUndo, failures);
            if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
            Console.WriteLine("PASS: image review checks completed using synthetic image files only.");
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); Directory.Delete(fixture, true); }
    }

    private static void Case(string name, Action action, List<string> failures)
    {
        try { action(); Console.WriteLine("PASS: " + name); }
        catch (Exception ex)
        {
            var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            failures.Add(name + ": " + error.Message);
            Console.WriteLine("FAIL: " + name + ": " + error);
        }
    }

    private static void SpatialAlignment()
    {
        NiftiPlane S(int units, float spacing, float shift = 0) => Plane(units,
            [spacing, 0, 0, shift, 0, spacing, 0, 0, 0, 0, spacing, 0]);
        Check(!S(1, .000020f).IsAlignedWith(S(1, .000025f)), "20 um and 25 um spacing must not align in meter units.");
        Check(S(1, .000020f, .001f).IsAlignedWith(S(2, .020f, 1)), "Equivalent meter/mm sforms must align.");
        Check(S(3, 20, 1000).IsAlignedWith(S(2, .020f, 1)), "Equivalent micron/mm sforms must align.");
        Check(!S(2, 1).IsAlignedWith(S(2, 1.000009f)), "Small per-step errors accumulating across 512 voxels must not align.");
        Check(!S(1, .000020f).IsAlignedWith(S(1, .000020f, .000005f)), "A quarter-voxel translation must not align.");
        Check(!S(0, 1).IsAlignedWith(S(2, 1)) && !S(0, 1).IsAlignedWith(S(0, 1)), "Unknown spatial units must not silently permit an overlay.");
        var oblique = new float[] { .016f, -.012f, 0, 1, .012f, .016f, 0, 2, 0, 0, .02f, 3 };
        var meters = oblique.Select(value => value / 1000).ToArray();
        Check(Plane(2, oblique).IsAlignedWith(Plane(1, meters)), "Equivalent translated oblique sforms must align.");
        var tilted = oblique.ToArray(); tilted[8] = .000005f;
        Check(!Plane(2, oblique).IsAlignedWith(Plane(2, tilted)), "Out-of-plane displacement along the x index must be checked.");
        var invalid = oblique.ToArray(); invalid[0] = float.NaN;
        Check(!Plane(2, oblique).IsAlignedWith(Plane(2, invalid)), "Non-finite transforms must be rejected.");
        var qMeters = new float[] { -1, .000020f, .000020f, .000020f, 0, 0, .38268343f, .001f, .002f, .003f };
        var qMm = qMeters.ToArray();
        foreach (var position in new[] { 1, 2, 3, 7, 8, 9 }) qMm[position] *= 1000;
        Check(QPlane(1, qMeters).IsAlignedWith(QPlane(2, qMm)), "Quaternion rotations and offsets must normalize only spatial values.");
        qMm[6] += .000005f;
        Check(!QPlane(1, qMeters).IsAlignedWith(QPlane(2, qMm)), "Oblique qform angular errors accumulating across the image must be rejected.");
    }

    private static NiftiPlane Plane(int units, float[] form, int width = 512, int height = 512) =>
        new(width, height, 2, 1, true, new byte[width * height], 1, 0, units, 0, 1, new float[10], form);
    private static NiftiPlane QPlane(int units, float[] form, int width = 512, int height = 512) =>
        new(width, height, 2, 1, true, new byte[width * height], 1, 0, units, 1, 0, form, new float[12]);

    private static void SingletonAlignment()
    {
        var original = new float[] { .000020f, 0, 0, 0, 0, .000020f, 0, 0, 0, 0, .000020f, 0 };
        var wider = original.ToArray(); wider[0] = .000025f;
        var taller = original.ToArray(); taller[5] = .000025f;
        Check(!Plane(1, original, 1, 512).IsAlignedWith(Plane(1, wider, 1, 512)), "A 1xN plane must reject a different voxel width even though its only x center is zero.");
        Check(!Plane(1, original, 512, 1).IsAlignedWith(Plane(1, taller, 512, 1)), "An Nx1 plane must reject a different voxel height.");
        Check(!Plane(1, original, 1, 1).IsAlignedWith(Plane(1, wider, 1, 1)), "One-pixel planes must compare their spatial footprints.");
        Check(Plane(1, original, 1, 1).IsAlignedWith(Plane(2, original.Select(value => value * 1000).ToArray(), 1, 1)),
            "Equivalent meter/mm voxel footprints must still align for a single pixel.");
        var q = new float[] { 1, .000020f, .000020f, .000020f, 0, 0, 0, 0, 0, 0 };
        var changed = q.ToArray(); changed[1] = .000025f;
        Check(!QPlane(1, q, 1, 512).IsAlignedWith(QPlane(1, changed, 1, 512)), "Singleton qform axes must compare voxel width too.");
    }

    private static void SourceRetention()
    {
        var a = WriteImage("retain-a.png", 32, 24, 10);
        var b = WriteImage("retain-b.png", 32, 24, 40);
        var viewer = Viewer(a, b);
        try
        {
            Load(viewer);
            AddMark(viewer);
            Await((Task)Invoke(viewer, "Move", 1)!);
            var stamp = File.GetLastWriteTimeUtc(a);
            var length = new FileInfo(a).Length;
            WriteImage("retain-a.png", 32, 24, 200);
            File.SetLastWriteTimeUtc(a, stamp);
            Check(new FileInfo(a).Length == length && File.GetLastWriteTimeUtc(a) == stamp,
                "The overwrite fixture must retain both length and timestamp.");
            var prompts = ResolveChanges(viewer, false, () => Await((Task)Invoke(viewer, "Move", -1)!));
            Check(BasePixel(Editor(viewer).RenderCopy([])) == 10, "Returning to overwritten A must retain its original pixels with its annotations.");
            Check(Editor(viewer).CanUndo && prompts > 0, "Changed source must explicitly offer to keep the original annotation work.");
            var output = Path.Combine(fixture, "retained-export.png");
            typeof(ImageViewerWindow).GetMethod("WritePng", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [output, Editor(viewer).RenderCopy(((ImageEdits)Field(Editor(viewer), "edits")!).Marks.ToArray())]);
            var exported = BrowserImage.LoadEditor(output, CancellationToken.None).Image;
            Check(BasePixel(exported) == 10 && Pixel(exported, 2, 2).R > 10,
                "Export must contain the original pixels and retained marks, without rereading replacement bytes.");
            try
            {
                typeof(ImageViewerWindow).GetMethod("WritePng", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [output, Editor(viewer).RenderCopy([])]);
                throw new Exception("PNG export must not overwrite an existing file.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            Check(BasePixel(BrowserImage.LoadEditor(output, CancellationToken.None).Image) == 10 &&
                !Directory.EnumerateFiles(fixture, ".pathdeck-*.tmp").Any(), "Failed export must preserve its destination and remove its own temporary file.");
            ResolveChanges(viewer, true, () => Load(viewer));
            Check(BasePixel(Editor(viewer).RenderCopy([])) == 200 && !Editor(viewer).CanUndo,
                "Explicit reload must use replacement pixels with a fresh annotation state.");
            AddMark(viewer);
            WriteImage("retain-a.png", 16, 12, 77);
            ResolveChanges(viewer, false, () => Load(viewer));
            Check(Editor(viewer).ImageDimensions == (32, 24) && Editor(viewer).CanUndo,
                "A differently sized replacement must not inherit old marks or replace their original dimensions.");
            ResolveChanges(viewer, true, () => Load(viewer));
            Check(Editor(viewer).ImageDimensions == (16, 12) && !Editor(viewer).CanUndo,
                "Discard/reload must replace dimensions and annotation state together.");
            AddMark(viewer); File.Delete(a); Load(viewer);
            Check(Editor(viewer).CanUndo && BasePixel(Editor(viewer).RenderCopy([])) == 77 &&
                ((string)Field(viewer, "saveStatus")!).Contains("保留的旧图"), "A missing source must retain exportable work and disclose that the old image is being edited.");
        }
        finally { Close(viewer); }
    }

    private static void DepartingSource()
    {
        var a = WriteImage("depart-a.png", 32, 24, 10);
        var b = WriteImage("depart-b.png", 32, 24, 40);
        var viewer = Viewer(a, b);
        try
        {
            Load(viewer); AddMark(viewer);
            WriteImage("depart-a.png", 32, 24, 90);
            var prompts = ResolveChanges(viewer, false, () => Await((Task)Invoke(viewer, "Move", 1)!));
            Check(prompts > 0 && BasePixel(Editor(viewer).RenderCopy([])) == 40,
                "Navigating away from an overwritten current image must disclose the source change and retain navigation order.");
            ResolveChanges(viewer, false, () => Await((Task)Invoke(viewer, "Move", -1)!));
            AddMark(viewer);
            var saves = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                foreach (var dialog in Application.Current.Windows.OfType<AnnotationSaveWindow>().Where(window => window.Owner == viewer).ToArray())
                { saves++; dialog.DialogResult = false; }
            };
            timer.Start();
            try { Await((Task)Invoke(viewer, "SaveCopy")!); }
            finally { timer.Stop(); }
            Check(saves == 1 && !(bool)Field(viewer, "saving")! && !Directory.EnumerateFiles(fixture, "*.tmp").Any(),
                "Canceling export must leave edits and files unchanged.");
            Check(Editor(viewer).CanUndo && BasePixel(Editor(viewer).RenderCopy([])) == 10, "Canceled export must retain the original work.");
        }
        finally { Close(viewer); }
    }

    private static void ComparisonSync()
    {
        var a = Viewer(WriteImage("sync-a.png", 32, 24, 10));
        var b = Viewer(WriteImage("sync-b.png", 32, 24, 20));
        ImageViewerWindow? c = null;
        try
        {
            Load(a); Load(b);
            var view = new ImageViewport(false, 2, new Point(.4, .3));
            Invoke(a, "SyncView", view);
            Check(Editor(b).Viewport == view, "Matching dimensions must continue synchronizing pan/zoom.");
            Invoke(a, "SyncPointer", new Point(4, 5));
            Check((Point?)Field(Editor(b), "linkedPointer") == new Point(4, 5), "Matching viewers must synchronize pointers.");
            c = Viewer(WriteImage("sync-c.png", 16, 12, 30));
            Load(c);
            var next = new ImageViewport(false, 3, new Point(.6, .7));
            Invoke(a, "SyncView", next);
            Check(Editor(b).Viewport == view && Editor(c).Viewport != next, "One dimension mismatch must pause the whole comparison group.");
        }
        finally { if (c is not null) Close(c); Close(b); Close(a); }
    }

    private static void SaveWorkflow()
    {
        var a = WriteImage("save-a.png", 32, 24, 10);
        var b = WriteImage("save-b.png", 32, 24, 40);
        var viewer = Viewer(a, b);
        try
        {
            Load(viewer); AddMark(viewer);
            var edits = (ImageEdits)Field(Editor(viewer), "edits")!;
            var saved = Path.Combine(fixture, "save-copy.png");
            SaveThroughDialog(viewer, saved);
            var exported = BrowserImage.LoadEditor(saved, CancellationToken.None).Image;
            Check(BasePixel(exported) == 10 && Pixel(exported, 2, 2).R > 10 && !edits.HasUnsavedMarks &&
                !(bool)Field(viewer, "saving")! && (string)Field(viewer, "saveStatus")! == "已另存标注 PNG",
                "Successful SaveCopy must write original pixels and marks, advance the saved baseline, and finish its UI state.");

            AddMark(viewer);
            var overwritten = Path.Combine(fixture, "save-after-overwrite.png");
            SaveThroughDialog(viewer, overwritten, _ => WriteImage("save-a.png", 32, 24, 90));
            Check(BasePixel(BrowserImage.LoadEditor(overwritten, CancellationToken.None).Image) == 10 && !edits.HasUnsavedMarks,
                "A source overwrite while the save dialog is open must export the already loaded original pixels.");

            AddMark(viewer);
            var staleSource = Path.Combine(fixture, "must-not-save-stale-source.png");
            SaveThroughDialog(viewer, staleSource, _ => ResolveChanges(viewer, true, () => Load(viewer)));
            Check(!File.Exists(staleSource) && edits.HasUnsavedMarks && BasePixel(Editor(viewer).RenderCopy([])) == 90 &&
                !Editor(viewer).CanUndo && !(bool)Field(viewer, "saving")!,
                "Replacing activeSource at the same index while a save dialog is open must reject that stale save without marking old edits saved.");

            AddMark(viewer);
            var departingEdits = (ImageEdits)Field(Editor(viewer), "edits")!;
            var staleIndex = Path.Combine(fixture, "must-not-save-wrong-index.png");
            SaveThroughDialog(viewer, staleIndex, _ => Await((Task)Invoke(viewer, "Move", 1)!));
            Check(!File.Exists(staleIndex) && departingEdits.HasUnsavedMarks && BasePixel(Editor(viewer).RenderCopy([])) == 40 &&
                (int)Field(viewer, "index")! == 1 && !Editor(viewer).CanUndo,
                "Navigating while the save dialog is open must reject that stale save and retain the selected-list position and old edits.");
            Check(!Directory.EnumerateFiles(fixture, ".pathdeck-*.tmp").Any(), "Save workflows must not leave temporary files.");
        }
        finally { Close(viewer); }
    }

    private static void SaveThroughDialog(ImageViewerWindow viewer, string destination, Action<AnnotationSaveWindow>? whileOpen = null)
    {
        ShowWithoutLoading(viewer);
        Exception? error = null;
        var accepted = 0;
        var operation = viewer.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<AnnotationSaveWindow>().Single(window => window.Owner == viewer && window.IsVisible);
            try
            {
                whileOpen?.Invoke(dialog);
                ((TextBox)dialog.FindName("FolderInput")).Text = Path.GetDirectoryName(destination)!;
                ((TextBox)dialog.FindName("NameInput")).Text = Path.GetFileNameWithoutExtension(destination);
                Invoke(dialog, "AcceptPath", null, new RoutedEventArgs());
                accepted++;
            }
            catch (Exception ex) { error = ex; dialog.DialogResult = false; }
        }), DispatcherPriority.Background);
        try { Await((Task)Invoke(viewer, "SaveCopy")!); }
        finally { if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort(); }
        if (error is not null) throw new Exception("Save dialog automation failed.", error);
        Check(accepted == 1, "SaveCopy must open and accept exactly one save dialog.");
    }

    private static void CanceledLoads()
    {
        var a = WriteImage("cancel-a.png", 32, 24, 10);
        var b = WriteImage("cancel-b.png", 32, 24, 40);
        var viewer = Viewer(a, b);
        var decoder = (SemaphoreSlim)typeof(ImageViewerWindow).GetField("Decoder", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            decoder.Wait();
            Task first, second;
            try { first = (Task)Invoke(viewer, "ShowImage")!; second = (Task)Invoke(viewer, "Move", 1)!; }
            finally { decoder.Release(); }
            Await(first); Await(second);
            Check(BasePixel(Editor(viewer).RenderCopy([])) == 40 && !Editor(viewer).CanUndo,
                "A superseded load must not publish pixels or edits into a later navigation request.");
            decoder.Wait();
            Task canceled;
            try { canceled = (Task)Invoke(viewer, "ShowImage")!; Invoke(viewer, "CancelLoad", null, EventArgs.Empty); }
            finally { decoder.Release(); }
            Await(canceled);
            Check(!Editor(viewer).HasImage && !Editor(viewer).CanExport, "Canceling the active load must leave no exportable stale image.");
        }
        finally { Close(viewer); }
    }

    private static void SavedUndo()
    {
        var state = new ImageEdits();
        state.Marks.Add(new ImageMark(ImageTool.Pen, Colors.Red, 1, new Point(1, 1)));
        state.MarkSaved(state.Marks.ToArray()); state.Undone.Push(state.Marks[0]); state.Marks.Clear();
        Check(state.HasUnsavedMarks, "Removing all marks after an export must remain an unsaved edit.");
    }

    private static void SourceReadSafety()
    {
        var binary = Path.Combine(fixture, "hash-cancel.bin");
        File.WriteAllBytes(binary, Enumerable.Range(0, 131072).Select(i => (byte)i).ToArray());
        using (var canceled = new CancellationTokenSource())
        using (var input = new CancelAfterReadStream(binary, canceled))
        {
            Check(!canceled.IsCancellationRequested, "Hashing cancellation must start after source bytes are read.");
            try
            {
                StaticInvoke("SourceVersion", input, null, canceled.Token);
                throw new Exception("Source hashing must stop after cancellation during a source read.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
            Check(input.BytesRead > 0 && input.Position < input.Length, "Canceled hashing must observe a completed read and stop before consuming the whole file.");
        }

        var image = WriteImage("writer-lock.png", 32, 24, 10);
        using (var locked = (FileStream)StaticInvoke("OpenSource", image)!)
        {
            var writer = Task.Run(() =>
            {
                try { using var file = new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); return false; }
                catch (IOException) { return true; }
            });
            Await(writer);
            Check(writer.Result, "The source lock must exclude a concurrent writer across fingerprinting and decoding.");
            Check(BasePixel(BrowserImage.LoadEditor(image, CancellationToken.None).Image) == 10,
                "The real decoder must still be able to read the locked source.");
            var version = (string)StaticInvoke("SourceVersion", locked, null, CancellationToken.None)!;
            Check(version == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image))) + ":",
                "Fingerprinting and the decoder must observe the same locked source content.");
        }
        using var released = new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Check(released.CanWrite, "Source read locks must be released after the load.");
    }

    private sealed class CancelAfterReadStream(string path, CancellationTokenSource cancellation)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        internal int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            BytesRead += count;
            if (count > 0) cancellation.Cancel();
            return count;
        }
    }

    private static void RasterInputLimits()
    {
        var path = Path.Combine(fixture, "oversized-sparse.png");
        using (var sparse = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            sparse.SetLength(BrowserImage.MaxFileSize + 1);
        try
        {
            using var opened = (FileStream)StaticInvoke("OpenSource", path)!;
            throw new Exception("The locked current file length must enforce the existing 64 MB raster limit before hashing.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException) { }
        using var released = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        Check(released.CanWrite, "Rejected oversized files must not retain a source lock.");
    }

    private static void SnapshotRetention()
    {
        var files = Enumerable.Range(0, 12).Select(i => WriteImage($"bounded-{i}.png", 32, 24, (byte)i)).ToArray();
        var viewer = Viewer(files);
        var decoder = (SemaphoreSlim)typeof(ImageViewerWindow).GetField("Decoder", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            Load(viewer);
            for (var i = 1; i < files.Length; i++) Await((Task)Invoke(viewer, "Move", 1)!);
            Check(((IDictionary)Field(viewer, "history")!).Count == 1, "Unedited navigation must retain only the active decoded snapshot.");
            AddMark(viewer);
            var edits = (ImageEdits)Field(Editor(viewer), "edits")!;
            edits.MarkSaved(edits.Marks.ToArray());
            Await((Task)Invoke(viewer, "Move", -1)!);
            Check(((IDictionary)Field(viewer, "history")!).Count == 2, "Saved annotations must retain their source binding too.");
            decoder.Wait();
            Task canceled;
            try { canceled = (Task)Invoke(viewer, "ShowImage")!; Invoke(viewer, "CancelLoad", null, EventArgs.Empty); }
            finally { decoder.Release(); }
            Await(canceled);
            Await((Task)Invoke(viewer, "Move", -1)!);
            Check(((IDictionary)Field(viewer, "history")!).Count == 2, "A canceled reload must not orphan unedited decoded snapshots during later navigation.");
        }
        finally { Close(viewer); }
    }

    private static ImageViewerWindow Viewer(params string[] paths) => new(paths.Select(path =>
        new ReviewItem(new FileEntry(path, Path.GetFileName(path), false, false, new FileInfo(path).Length, File.GetLastWriteTime(path)))).ToArray(), 0)
        { ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
    private static void Load(ImageViewerWindow viewer) => Await((Task)Invoke(viewer, "ShowImage")!);
    private static ImageEditorCanvas Editor(ImageViewerWindow viewer) => (ImageEditorCanvas)viewer.FindName("Editor");
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static object? Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static object? StaticInvoke(string name, params object?[] args) => typeof(ImageViewerWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    private static void AddMark(ImageViewerWindow viewer) => ((ImageEdits)Field(Editor(viewer), "edits")!).Marks
        .Add(new ImageMark(ImageTool.Pen, Colors.Red, 3, new Point(2, 2)));
    private static void Close(ImageViewerWindow viewer)
    {
        ((IDictionary)Field(viewer, "history")!).Clear();
        viewer.Close();
    }

    private static int ResolveChanges(ImageViewerWindow viewer, bool discard, Action action)
    {
        var count = 0;
        ShowWithoutLoading(viewer);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<Window>().Where(window => window is not AnnotationSaveWindow && window.Owner == viewer && window.IsVisible).ToArray())
            { count++; dialog.DialogResult = discard; }
        };
        timer.Start();
        try { action(); return count; }
        finally { timer.Stop(); }
    }

    private static void ShowWithoutLoading(ImageViewerWindow viewer)
    {
        // ShowDialog requires a visible owner. Suppress the Loaded handler's extra decode.
        viewer.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), viewer,
            viewer.GetType().GetMethod("LoadImage", Private)!);
        if (!viewer.IsVisible) viewer.Show();
    }

    private static string WriteImage(string name, int width, int height, byte value)
    {
        var path = Path.Combine(fixture, name);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null,
            Enumerable.Repeat(value, width * height).ToArray(), width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write); encoder.Save(stream);
        return path;
    }
    private static byte BasePixel(BitmapSource source) => Pixel(source, source.PixelWidth - 1, source.PixelHeight - 1).B;
    private static Color Pixel(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    private static void Await(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Image check did not complete.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
