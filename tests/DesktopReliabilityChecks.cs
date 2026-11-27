using System.Drawing;
using System.Reflection;
using System.Threading;
using TaskGlass;

internal static class DesktopReliabilityChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static void Run()
    {
        FileModuleReuse();
        LateForegroundNotice();
        MonitorSceneCache();
        Console.WriteLine("PASS: desktop relaunch dispatch, late foreground notification dismissal, full touched-monitor drag cache and negative/offscreen native-pixel geometry. Synthetic data only.");
    }

    private static void FileModuleReuse()
    {
        var method = typeof(QuickFileDesk.App).Assembly.GetType("QuickFileDesk.Program")!.GetMethod("TryReuseFileModule", All);
        Check(method is not null, "Existing file-module navigation must dispatch desktop restart through a testable policy.");
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--file-settings" }, new[] { "--background" } })
        {
            string testName = @"Local\PathDeck.Checks.Reuse." + Guid.NewGuid();
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, testName);
            int starts = 0;
            string? requested = null;
            bool reused = (bool)method!.Invoke(null, new object[]
            {
                args, (Action)(() => starts++), (Func<string, EventWaitHandle>)(name =>
                { requested = name; return EventWaitHandle.OpenExisting(testName); })
            })!;
            Check(reused && starts == 1, "Normal, file-settings and background relaunches must restart an absent desktop module when files are already running.");
            Check(signal.WaitOne(0) == !args.Contains("--background"), "Background reuse must remain hidden; normal and settings reuse must signal their destination.");
            Check(requested == (args.Contains("--file-settings") ? @"Local\PathDeck.Files.Settings" : @"Local\PathDeck.Files.Show"), "Reuse must retain the correct file-navigation destination.");
        }
        foreach (var args in new[] { new[] { "--stop" }, new[] { "--desktop-module", "--stop" }, new[] { "--background", "--stop" } })
        {
            int actions = 0;
            bool reused = (bool)method!.Invoke(null, new object[]
            {
                args, (Action)(() => actions++), (Func<string, EventWaitHandle>)(_ =>
                { actions++; throw new Exception("A stop command must not open file navigation."); })
            })!;
            Check(!reused && actions == 0, "Stop commands must not restart the desktop module or signal file navigation.");
        }
        int missingStarts = 0;
        bool missing = (bool)method!.Invoke(null, new object[]
        {
            Array.Empty<string>(), (Action)(() => missingStarts++), (Func<string, EventWaitHandle>)(_ => throw new WaitHandleCannotBeOpenedException())
        })!;
        Check(!missing && missingStarts == 0, "A missing file-navigation owner must fall through to normal startup.");
    }

    private static void LateForegroundNotice()
    {
        var constructor = typeof(NotificationHub).GetConstructor(All, null, new[] { typeof(Func<nint>), typeof(Func<nint, (string?, string?)>) }, null);
        Check(constructor is not null, "Foreground notification matching must accept synthetic window/source lookups.");
        string? appId = "Synthetic.Mail", app = null;
        using var hub = (NotificationHub)constructor!.Invoke(new object[]
        {
            (Func<nint>)(() => 101), (Func<nint, (string?, string?)>)(_ => (appId, app))
        });
        hub.SetPaused(true);
        var unrelated = new Notice("synthetic:other", "Other", "Other source", "Synthetic body") { SourceAppId = "Synthetic.Other" };
        hub.Post(unrelated);
        hub.Post(new Notice("synthetic:late", "Mail", "Queued before activation", "Synthetic body") { SourceAppId = appId });
        Check(hub.Store.Items.Count == 1 && ReferenceEquals(hub.Store.Items[0], unrelated), "A queued notice posted after its source is already foreground must disappear without another HWND change.");
        Check(hub.Received["Mail"] == 1 && hub.LastPopupAt is null, "Foreground notices retain received counters without displaying a late popup.");
        appId = null; app = "微信";
        hub.Post(new Notice("synthetic:wechat", "微信", "Synthetic WeChat", "Synthetic body"));
        Check(hub.Store.Items.Count == 1, "Client notifications must also dismiss when WeChat was foreground before delivery.");
        app = "阿里邮箱";
        hub.Post(new Notice("synthetic:alimail", "阿里邮箱", "Synthetic mail", "Synthetic body"));
        Check(hub.Store.Items.Count == 1, "Client notifications must also dismiss when AliMail was foreground before delivery.");
        bool opened = false;
        app = null;
        var openable = new Notice("synthetic:open", "Other", "Open source", "Synthetic body", () => opened = true);
        hub.Post(openable); hub.Open(openable);
        Check(opened && hub.Store.Items.Count == 1 && ReferenceEquals(hub.Store.Items[0], unrelated), "Opening one card must activate only its own source and preserve unrelated cards.");
        hub.MarkRead(unrelated.Key);
        Check(hub.Store.Items.Count == 0, "Per-card dismissal must still remove its matching card.");
    }

    private static void MonitorSceneCache()
    {
        var method = typeof(GlassSnapshot).GetMethod("SceneBounds", All);
        Check(method is not null, "Scene capture must expose native-pixel monitor geometry without taking screenshots.");
        Rectangle? Scene(Rectangle window, Rectangle[] monitors, bool hidden = false, Rectangle? restoreMonitor = null) =>
            (Rectangle?)method!.Invoke(null, new object?[] { window, monitors, hidden, restoreMonitor });
        var primary = new Rectangle(0, 0, 1920, 1080);
        var right = new Rectangle(1920, 0, 1920, 1080);
        var screens = new[] { primary, right };
        Check(Scene(new Rectangle(100, 100, 800, 600), screens) == primary, "Ordinary drags must cache the full primary monitor.");
        Check(Scene(new Rectangle(1120, 100, 800, 600), screens) == primary, "A window merely touching a monitor edge must not sample the next monitor.");
        Check(Scene(new Rectangle(1140, 100, 800, 600), screens) == Rectangle.Union(primary, right), "Cross-screen capture must include the complete bounds of both touched monitors.");
        Rectangle? cache = null;
        int misses = 0;
        for (int left = 200; left <= 3000; left += 20)
        {
            var viewport = new Rectangle(left, 100, 800, 600);
            if (cache?.Contains(viewport) == true) continue;
            cache = Scene(viewport, screens); misses++;
        }
        Check(misses == 2, $"An 800-pixel window crossing two 1920-pixel monitors must need only two cache fills, not {misses}.");
        var negative = new Rectangle(-2560, -200, 2560, 1440);
        Check(Scene(new Rectangle(-300, -50, 800, 600), new[] { negative, primary }) == Rectangle.Union(negative, primary), "Mixed-size monitors at negative native-pixel coordinates must retain their complete bounds.");
        Check(Scene(new Rectangle(-2800, 100, 800, 600), new[] { negative, primary }) == negative, "Partly offscreen windows must stay bounded to their touched monitor.");
        var offscreen = new Rectangle(-6000, 100, 800, 600);
        Check(Scene(offscreen, new[] { negative, primary }) is null, "Fully offscreen visible windows must not cause a screen capture.");
        Check(Scene(offscreen, new[] { negative, primary }, hidden: true) == negative, "Hidden preparation must still use the nearest full monitor for offscreen/minimized windows.");
        Check(Scene(new Rectangle(-32000, -32000, 800, 600), screens, hidden: true, restoreMonitor: right) == right, "A minimized sentinel rectangle must prepare the supplied HWND restore monitor instead of the nearest monitor to -32000.");
        Check(Scene(new Rectangle(100, 100, 800, 600), screens, hidden: true, restoreMonitor: right) == primary, "A hidden monitor fallback must not replace full touched-monitor geometry when the window has visible bounds.");
        Check(Scene(new Rectangle(1700, 100, 800, 600), new[] { negative, primary, right }) == Rectangle.Union(primary, right), "A scene must include touched monitors without expanding to unrelated monitors.");
        Check(Scene(primary, Array.Empty<Rectangle>(), hidden: true) is null, "Unavailable monitor geometry must skip capture safely.");
    }
}
