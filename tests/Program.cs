using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickFileDesk;
using TaskGlass;

static class Program
{
    // WPF queues OnStartup in the constructor; pumping a synthetic dialog must never start engines.
    private sealed class CheckApplication : QuickFileDesk.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    [STAThread]static int Main(string[] args)
    {
        var app = new CheckApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // BAML's App root requires an exact type; the isolated host loads the same production dictionaries.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/PathDeck;component/ThemeLight.xaml",UriKind.Relative)});
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/PathDeck;component/ControlStyles.xaml",UriKind.Relative)});
        GlassMenus.Install(app);
        string? previous = File.Exists(Preferences.FilePath) ? File.ReadAllText(Preferences.FilePath) : null;
        try
        {
            if(args.Contains("--contrast")) { TextContrastChecks.Run(app); return 0; }
            if(args.Contains("--settings-contrast")) { GlassSettingsChecks.Run(typeof(TaskGlass.SettingsWindow).Assembly); return 0; }
            if(args.Contains("--desktop-reliability")) { DesktopReliabilityChecks.Run(); return 0; }
            if(args.Contains("--core")) { FileSearchChecks.Run(); ImageReviewChecks.Run(); DesktopReliabilityChecks.Run(); TextContrastChecks.Run(app); return 0; }
            if(args.Contains("--clock-progress")) { ClockProgressChecks.Run(); return 0; }
            if(args.Contains("--clock-preview")) { ClockProgressChecks.Run(@"D:\codex\.tmp\pathdeck-clock-progress-20261127\clock-preview.png"); return 0; }
            if(args.Contains("--performance")) { BackdropPerformance.Run(); return 0; }
            if(args.Contains("--drag-rendering")) { DragRenderingChecks.Run(args); return 0; }
            if(args.Contains("--desktop-blur")) { DesktopBlurChecks.Run(); return 0; }
            if(args.Contains("--desktop-fold")) { DesktopFoldingChecks.Run(); return 0; }
            if(args.Contains("--win-e-backdrop")) { WinEBackdropChecks.Run(app); return 0; }
            if(args.Contains("--menus")) { MenuStyleChecks.Run(); return 0; }
            if(args.Contains("--wallpaper-read")) { var wallpaper=DesktopWallpaper.Read(); Console.WriteLine($"Wallpaper scene ready: {wallpaper.Image.PixelWidth}×{wallpaper.Image.PixelHeight}"); return 0; }
            var assembly=typeof(QuickFileDesk.App).Assembly;
            if(assembly.GetType("QuickFileDesk.DesktopModule") is null)throw new Exception("PathDeck needs an integrated desktop module behind the same executable entry.");
            var desktop = typeof(TaskGlass.SettingsWindow).Assembly;
            GlassChecks.Run(desktop);
            GlassShaderChecks.Run(desktop);
            GlassSettingsChecks.Run(desktop);
            TextContrastChecks.Run(app);
            DesktopReliabilityChecks.Run();
            FileSearchChecks.Run();
            ImageReviewChecks.Run();
            ModuleChecks.Run(desktop);
            CachedAppearanceChecks.Run();
            ClockProgressChecks.Run();
            DesktopBlurChecks.Run();
            DesktopFoldingChecks.Run();
            MenuStyleChecks.Run();
            SettingsLiveBackdropChecks.Run(desktop);
            WinEBackdropChecks.Run(app);
            IntegrationChecks(app);
            const BindingFlags fields=BindingFlags.NonPublic|BindingFlags.Instance;
            foreach(string field in new[]{"navigationOwner","filesWait","fileSettingsWait","shortcutOwner"})
                Check(typeof(QuickFileDesk.App).GetField(field,fields)!.GetValue(app) is null,"Synthetic tests must never start navigation IPC, global shortcuts or production engines.");
            Console.WriteLine("PASS: synthetic test application suppresses deferred production startup; no navigation IPC or keyboard-hook ownership created.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally
        {
            if(previous is null) File.Delete(Preferences.FilePath); else File.WriteAllText(Preferences.FilePath,previous);
            app.Shutdown();
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_,_) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void IntegrationChecks(QuickFileDesk.App app)
    {
        Check(Preferences.Root.StartsWith(AppContext.BaseDirectory), "Checks must use their own portable settings, not the installed application.");
        var original = new Preferences(10,24,false,11) { TaskbarColor="#242B2D" };
        original.Save();
        using var appearance = new SharedAppearance(app);
        var content = new TextBox { Text="像素和文字保持清晰", Margin=new Thickness(30), Height=50, VerticalAlignment=VerticalAlignment.Top };
        var backdrop = new Window { Left=120,Top=120,Width=900,Height=660,ShowInTaskbar=false,Content=new Grid { Background=new LinearGradientBrush(Colors.DodgerBlue,Colors.Orange,0) } };
        var window = new Window { Left=170,Top=165,Width=700,Height=470,Title="PathDeck · 共享材质检查", Content=content,ShowInTaskbar=false };
        var glass = new WindowGlass(window,appearance);
        window.SourceInitialized += (_,_) => WindowsIntegration.SetWindowFrameTheme(window,app.DarkMode);
        try
        {
            backdrop.Show(); window.Show(); Pump(450);
            Check(window.Opacity==1 && content.Opacity==1 && content.Effect is null, "Material changes must not dim text or image contents.");
            Check(glass.Layers.Children.Count==2 && ReferenceEquals(glass.Layers.Children[1],content), "The foreground must remain outside the background shader.");
            var materialLayer=(Border)glass.Layers.Children[0];
            var sample=(Image)materialLayer.Child;
            Check(sample.Source is BitmapSource, "The file window must have a cached backdrop with the shared material.");
            Check(app.DarkMode && ((SolidColorBrush)app.Resources["Text"]).Color==Colors.White, "Dark tint needs the same readable foreground as the desktop module.");
            var changed = original with { TaskbarColor="#F7F9F9",Opacity=28,Blur=35 };
            changed.Save(); Pump(850);
            Check(!app.DarkMode && appearance.Preferences.TaskbarColor==changed.TaskbarColor, "Saved shared appearance must update file windows without reopening.");
            Check(Math.Abs(((System.Windows.Media.Effects.BlurEffect)((BackdropImage)sample).Pixels.Effect).Radius-21)<.001, "Blur uses the desktop module's same radius mapping.");
            window.Left+=70; window.Width+=40; Pump(300);
            Check(sample.Source is BitmapSource, "Move and resize must retain the cached backdrop.");
            window.Hide(); Pump(60);
            Check(sample.Source is null, "Hidden windows must release captured images.");
        }
        finally { window.Close(); backdrop.Close(); }

        original.Save(); Pump(800);
        var stub=(GlassApplication)RuntimeHelpers.GetUninitializedObject(typeof(GlassApplication));
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        typeof(GlassApplication).GetField("preferences",flags)!.SetValue(stub,original);
        typeof(GlassApplication).GetField("<State>k__BackingField",flags)!.SetValue(stub,"本地整合检查 · 未启动消息采集");
        var settings=new TaskGlass.SettingsWindow(stub);
        try
        {
            settings.Show(); Pump(250);
            var navigation=(ListBox)settings.FindName("SectionNavigation");
            Check(!navigation.Items.Cast<string>().Contains("文件管理"), "Desktop settings must keep file-management preferences in the file manager's own settings.");
            Check(navigation.Items.Cast<string>().Contains("外观与动画")&&navigation.Items.Cast<string>().Contains("状态栏"), "Desktop settings must retain shared appearance and desktop controls.");
            Check(navigation.Items.Cast<string>().Contains("任务栏"), "Work hours must be reachable from the desktop/taskbar settings entry.");
            navigation.SelectedItem="任务栏";Pump(150);
            var workStart=(TextBox)settings.GetType().GetField("workStartField",flags)!.GetValue(settings)!;
            var workEnd=(TextBox)settings.GetType().GetField("workEndField",flags)!.GetValue(settings)!;
            workStart.Text="18:00";workEnd.Text="08:00";
            settings.GetType().GetMethod("SaveWorkHours",flags)!.Invoke(settings,null);
            Check(((TextBlock)settings.GetType().GetField("workHoursError",flags)!.GetValue(settings)!).Visibility==Visibility.Visible&&stub.Preferences.WorkStart==original.WorkStart, "Invalid work hours must show an inline error without changing preferences.");
            Check(settings.Title.StartsWith("PathDeck"), "The merged desktop module must use the PathDeck product name.");
        }
        finally { typeof(GlassApplication).GetField("<Exiting>k__BackingField",flags)!.SetValue(stub,true); settings.Close(); }
        var fileSettings=new QuickFileDesk.SettingsWindow();
        Check(fileSettings.FindName("LightTheme") is null && fileSettings.FindName("DarkTheme") is null, "File-specific preferences must not expose competing appearance controls.");
        Check(fileSettings.FindName("StartupToggle") is null, "Startup belongs to the unified application settings.");
        fileSettings.Close();
        string backup=SettingsBackup.Serialize(original,LauncherData.CreateDefaults().ToJson());
        Check(SettingsBackup.Parse(backup.Replace("\"PathDeck\"","\"TaskGlass\"")).Preferences.TaskbarColor==original.TaskbarColor, "Legacy TaskGlass backups must remain importable.");
        Check(Startup.Command.EndsWith("PathDeck.exe\" --background"), "One PathDeck background command must start both modules at login.");
        var fixture=TestWorkspace.NewDirectory("browser");
        var browser=new QuickFileDesk.MainWindow(fixture);
        _=new WindowGlass(browser,appearance);
        browser.SourceInitialized+=(_,_)=>WindowsIntegration.SetWindowFrameTheme(browser,app.DarkMode);
        var browserField=typeof(QuickFileDesk.App).GetField("browser",flags)!;
        var appearanceField=typeof(QuickFileDesk.App).GetField("appearance",flags)!;
        object? oldBrowser=browserField.GetValue(app),oldAppearance=appearanceField.GetValue(app);var oldMain=app.MainWindow;
        try
        {
            browser.Show(); Pump(650);
            Check(browser.FindName("SettingsButton") is Button && browser.FindName("Tabs") is TabControl, "The unified skin must retain browser controls and tabs.");
            browserField.SetValue(app,browser);appearanceField.SetValue(app,appearance);app.MainWindow=browser;
            ((Button)browser.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(150);
            var ownSettings=app.Windows.OfType<QuickFileDesk.SettingsWindow>().SingleOrDefault();
            Check(ownSettings is not null&&ownSettings.Owner==browser, "The browser gear must open file-management settings owned by that browser.");
            ((Button)browser.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(100);
            Check(app.Windows.OfType<QuickFileDesk.SettingsWindow>().Count()==1, "Repeated settings clicks must reuse the existing file-management settings window.");
            var changed=original with {TaskbarColor="#F7F9F9",Opacity=28,Blur=35};changed.Save();Pump(850);
            Check(!app.DarkMode&&appearance.Preferences.Opacity==28&&appearance.Preferences.Blur==35, "Separating settings must preserve live shared color, opacity and blur updates.");
            Check(((System.Windows.Media.Effects.BlurEffect)((BackdropImage)((Border)((Grid)ownSettings!.Content).Children[0]).Child).Pixels.Effect).Radius==21, "File-management settings must follow the shared glass blur too.");
        }
        finally
        {
            foreach(var owned in app.Windows.OfType<QuickFileDesk.SettingsWindow>().ToArray())owned.Close();browser.Close();
            browserField.SetValue(app,oldBrowser);appearanceField.SetValue(app,oldAppearance);app.MainWindow=oldMain;
        }
        Console.WriteLine("PASS: separate file/desktop settings routes, shared live color/opacity/blur, client-only backdrop, background-only effects, lifecycle and legacy backup compatibility.");
    }
}
