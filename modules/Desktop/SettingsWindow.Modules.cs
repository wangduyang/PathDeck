using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace TaskGlass;

public partial class SettingsWindow
{
    private readonly List<Border> sections = new();
    private bool readingModules;
    private CheckBox? animationCheck, statusCheck, fullscreenCheck, secondsCheck, lunarCheck, launcherCheck, controlCheck;
    private ComboBox? performanceChoice, monitorChoice;
    private TextBox? shortcutField;
    private TextBox? workStartField,workEndField;
    private TextBlock? workHoursError;
    private TextBlock? moduleStatus;
    private ListBox? componentList;
    private readonly TranslateTransform pageShift = new();
    private string lastPage = "";
    private static readonly Dictionary<string, string> captions = new()
    { ["Launcher"]="启动台",["Clock"]="日期与时间",["Resources"]="资源占用",["Input"]="输入法",["Notifications"]="待处理通知",["Tray"]="系统托盘",["Controls"]="控制中心" };
    private void BuildModules()
    {
        SectionNavigation.ItemsSource = new[] { "外观与动画", "任务栏", "启动台", "状态栏", "控制中心", "通知", "桌面分区", "快捷键", "性能", "备份与关于" };
        sections.AddRange(PageContent.Children.OfType<Border>());
        StackPanel Add(string category, string title, string subtitle)
        {
            var body = new StackPanel(); body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SettingsSectionTitle") });
            body.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("SettingsMuted"), Margin = new Thickness(0,8,0,12) });
            var card = new Border { Tag = category, Style = (Style)FindResource("SettingsCard"), Margin = new Thickness(0,16,0,0), Child = body };
            sections.Add(card); PageContent.Children.Insert(PageContent.Children.Count - 1, card); return body;
        }
        CheckBox Toggle(StackPanel body, string text, bool value, Func<Preferences, bool, Preferences> change)
        {
            var check = new CheckBox { Content = text, IsChecked = value };
            check.Click += (_, _) => { if (readingModules) return; ChangeModules(change(app.Preferences, check.IsChecked == true)); };
            body.Children.Add(check); return check;
        }
        void ActionButton(StackPanel body, string text, Action action)
        { var button = new Button { Content = text, Margin = new Thickness(0,8,0,0), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => action(); body.Children.Add(button); }
        var appearance = Add("外观与动画", "细腻的反馈，轻盈的过渡", "面板进出、悬停按压与通知补位使用统一动效。关闭动画时即时响应，也尊重 Windows 的动画设置。");
        animationCheck = Toggle(appearance, "界面动画", app.Preferences.AnimationEnabled, (p,v)=>p with { AnimationEnabled=v });
        var clock=Add("任务栏","时钟与一天进度","左下角时钟背景表示 00:00–24:00 的一天进度，每分钟更新。已过去的时间用亮绿色填充，工作时段更亮、前后自然渐变；未到的时间保留原玻璃背景。");
        var hours=new Grid{Margin=new Thickness(0,4,0,0),HorizontalAlignment=HorizontalAlignment.Left};
        hours.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});hours.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});hours.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        TextBox TimeField(string label,string value,int column)
        {
            var group=new StackPanel();group.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,0,0,8)});
            var field=new TextBox{Text=value,MaxLength=5,Width=140,MinHeight=44,FontSize=18,HorizontalContentAlignment=HorizontalAlignment.Center,ToolTip="24 小时制，例如 08:30"};
            Typography.SetNumeralAlignment(field,FontNumeralAlignment.Tabular);
            System.Windows.Automation.AutomationProperties.SetName(field,label);group.Children.Add(field);Grid.SetColumn(group,column);hours.Children.Add(group);return field;
        }
        workStartField=TimeField("工作开始",app.Preferences.WorkStart,0);workEndField=TimeField("工作结束",app.Preferences.WorkEnd,2);clock.Children.Add(hours);
        workHoursError=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Visibility=Visibility.Collapsed};
        workHoursError.SetResourceReference(TextBlock.ForegroundProperty,"SettingsTextBrush");clock.Children.Add(workHoursError);
        ActionButton(clock,"保存工作时段",SaveWorkHours);
        clock.Children.Add(new TextBlock{Text="保存后，左下角时钟背景立即同步。",Style=(Style)FindResource("SettingsMuted"),Margin=new Thickness(0,10,0,0)});
        var launch = Add("启动台", "应用，随手可达", "使用原始应用图标。分类整理和拖拽排序只改变启动台，不移动桌面图标或源文件。");
        launcherCheck=Toggle(launch,"启用启动台",app.Preferences.LauncherEnabled,(p,v)=>p with {LauncherEnabled=v});
        fullscreenCheck = Toggle(launch, "全屏网格（默认使用紧凑面板）", app.Preferences.LauncherFullscreen, (p,v)=>p with { LauncherFullscreen=v });
        ActionButton(launch, "打开启动台", app.ShowLauncher);
        var bar = Add("状态栏", "信息留在顶部", "时间、资源和快捷入口统一在玻璃状态栏中。最大化窗口会避让；退出后恢复可用空间。");
        statusCheck = Toggle(bar, "显示顶部状态栏", app.Preferences.ShowStatusBar, (p,v)=>p with { ShowStatusBar=v });
        secondsCheck = Toggle(bar, "时钟显示秒数", app.Preferences.ClockSeconds, (p,v)=>p with { ClockSeconds=v });
        lunarCheck = Toggle(bar, "显示农历", app.Preferences.LunarDate, (p,v)=>p with { LunarDate=v });
        bar.Children.Add(new TextBlock { Text="显示器", Margin=new Thickness(0,12,0,8) });
        monitorChoice = new ComboBox { DisplayMemberPath="Label", SelectedValuePath="Key" };
        monitorChoice.Items.Add(new MonitorOption("primary","主显示器")); monitorChoice.Items.Add(new MonitorOption("all","全部显示器"));
        foreach (var screen in Forms.Screen.AllScreens) monitorChoice.Items.Add(new MonitorOption(screen.DeviceName, screen.DeviceName));
        monitorChoice.SelectedValue=app.Preferences.StatusMonitor;
        monitorChoice.SelectionChanged+=(_,_)=> { if (!readingModules && monitorChoice.SelectedValue is string key) ChangeModules(app.Preferences with { StatusMonitor=key }); };
        bar.Children.Add(monitorChoice);
        bar.Children.Add(new TextBlock { Text="组件与显示顺序", Margin=new Thickness(0,16,0,8) });
        componentList=new ListBox { MinHeight=200 }; bar.Children.Add(componentList); ReadComponents();
        var order=new StackPanel { Orientation=Orientation.Horizontal }; bar.Children.Add(order);
        foreach(var (label,delta) in new[]{("上移",-1),("下移",1)})
        {
            var button=new Button { Content=label, Margin=new Thickness(0,8,8,0) };
            button.Click+=(_,_)=>MoveComponent(delta); order.Children.Add(button);
        }
        var controls=Add("控制中心","常用控制，一个面板","音量、静音、网络、蓝牙与电池状态。设备不支持的功能会明确说明，并提供对应系统设置入口。");
        controlCheck=Toggle(controls,"启用控制中心",app.Preferences.ControlCenterEnabled,(p,v)=>p with {ControlCenterEnabled=v});
        ActionButton(controls,"打开控制中心",app.ShowControlCenter);
        var shortcuts=Add("快捷键","直接打开启动台","组合键至少包含 Ctrl、Alt 或 Shift。不接管 Windows 键；快捷键被占用时会显示原因。");
        shortcutField=new TextBox { Text=app.Preferences.LauncherHotkey }; shortcuts.Children.Add(shortcutField);
        ActionButton(shortcuts,"应用快捷键",()=> { if (!LauncherHotkey.IsValid(shortcutField.Text)) { StatusText.Text="请输入 Ctrl+Alt+Space 等有效组合键。"; return; } ChangeModules(app.Preferences with { LauncherHotkey=shortcutField.Text }); });
        moduleStatus=new TextBlock { Text=app.HotkeyStatus, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,12,0,0) }; shortcuts.Children.Add(moduleStatus);
        var performance=Add("性能","效果与日常使用的平衡","节能模式关闭过渡动画、降低资源刷新频率；保留你保存的颜色和模糊强度。静止时不连续重绘动画。");
        performanceChoice=new ComboBox { ItemsSource=new[]{"节能","均衡","完整效果"}, SelectedItem=app.Preferences.PerformancePreset };
        performanceChoice.SelectionChanged+=(_,_)=> { if (!readingModules && performanceChoice.SelectedItem is string choice) ChangeModules(app.Preferences with { PerformancePreset=choice }); };
        performance.Children.Add(performanceChoice);
        var backup=Add("备份与关于","你的桌面，可以带走","备份外观、动画、模块、快捷键、启动台分类和搜索引擎。导入前完整校验，失败时保持当前设置。");
        ActionButton(backup,"导出配置…",ExportConfiguration); ActionButton(backup,"导入配置…",ImportConfiguration);
        foreach (var category in SectionNavigation.Items.Cast<string>().Where(c=>c!="备份与关于" && c!="桌面分区"))
        { var body=Add(category,"恢复这一页","只恢复本页的偏好，保留其他模块及启动台分类。"); ActionButton(body,"恢复本页默认值",()=>ResetSection(category)); }
        SectionNavigation.SelectedIndex=0; FilterSections();
    }
    private sealed record MonitorOption(string Key,string Label);
    private void ChangeModules(Preferences value)
    {
        try { app.Update(value); StatusText.Text=""; ReadModules(); }
        catch(Exception error) { StatusText.Text=error.Message; ReadModules(); }
    }
    internal void ReadModules()
    {
        if(animationCheck is null) return; readingModules=true;
        try
        {
            var p=app.Preferences; animationCheck.IsChecked=p.AnimationEnabled; statusCheck!.IsChecked=p.ShowStatusBar;
            launcherCheck!.IsChecked=p.LauncherEnabled;controlCheck!.IsChecked=p.ControlCenterEnabled;
            fullscreenCheck!.IsChecked=p.LauncherFullscreen; secondsCheck!.IsChecked=p.ClockSeconds; lunarCheck!.IsChecked=p.LunarDate;
            performanceChoice!.SelectedItem=p.PerformancePreset; monitorChoice!.SelectedValue=p.StatusMonitor;
            shortcutField!.Text=p.LauncherHotkey; moduleStatus!.Text=app.HotkeyStatus; ReadComponents();
            workStartField!.Text=p.WorkStart;workEndField!.Text=p.WorkEnd;
        }
        finally { readingModules=false; }
    }
    private void SaveWorkHours()
    {
        string start=workStartField!.Text.Trim(),end=workEndField!.Text.Trim();
        if(!Preferences.TryWorkHours(start,end,out _,out _))
        {workHoursError!.Text="请输入 HH:mm 格式的时间，且结束时间须晚于开始时间。";workHoursError.Visibility=Visibility.Visible;return;}
        workHoursError!.Visibility=Visibility.Collapsed;ChangeModules(app.Preferences with {WorkStart=start,WorkEnd=end});
    }
    private void ReadComponents()
    {
        if(componentList is null) return; string? selectedKey=(componentList.SelectedItem as ListBoxItem)?.Tag as string; componentList.Items.Clear();
        foreach(string key in app.Preferences.StatusComponents.Concat(Preferences.ComponentNames).Distinct())
        {
            var check=new CheckBox { Content=captions[key], IsChecked=app.Preferences.StatusComponents.Contains(key), Tag=key };
            check.Click+=(_,_)=> { var list=componentList.Items.Cast<ListBoxItem>().Select(i=>(CheckBox)i.Content).Where(c=>c.IsChecked==true).Select(c=>(string)c.Tag).ToArray(); ChangeModules(app.Preferences with { StatusComponents=list }); };
            var item=new ListBoxItem { Content=check, Tag=key }; componentList.Items.Add(item);
            check.PreviewMouseLeftButtonDown+=(_,_)=>componentList.SelectedItem=item;
            if(key==selectedKey)componentList.SelectedItem=item;
        }
    }
    private void MoveComponent(int delta)
    {
        if(componentList?.SelectedItem is not ListBoxItem selected || selected.Tag is not string key) return;
        var values=app.Preferences.StatusComponents.ToList(); int index=values.IndexOf(key), next=index+delta;
        if(index<0 || next<0 || next>=values.Count) return; (values[index],values[next])=(values[next],values[index]);
        ChangeModules(app.Preferences with { StatusComponents=values.ToArray() });
        componentList.SelectedItem=componentList.Items.Cast<ListBoxItem>().FirstOrDefault(i=>(string)i.Tag==key);
    }
    private void SearchSettings(object sender, TextChangedEventArgs e) { if(PageContent is not null) FilterSections(); }
    private void SelectSection(object sender, SelectionChangedEventArgs e)
    { if(PageContent is null) return; if(SettingsSearch is not null) SettingsSearch.Text=""; FilterSections(); }
    private void FilterSections()
    {
        string category=SectionNavigation.SelectedItem as string ?? "外观与动画", query=SettingsSearch.Text.Trim();
        bool pageChanged=query.Length==0 && lastPage!=category;lastPage=category;
        PageTitle.Text=query.Length>0 ? "搜索设置" : category;
        PageSubtitle.Text=query.Length>0 ? "与“"+query+"”相关的设置" : category=="外观与动画" ? "文件管理与桌面模块共用同一套玻璃外观" : "按你的习惯，调整桌面";
        foreach(var card in sections) card.Visibility=(query.Length==0 ? Equals(card.Tag,category) : SearchText(card).Contains(query,StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        if(GlassMotion.Active && pageChanged) { PageContent.RenderTransform=pageShift;pageShift.X=8;GlassMotion.To(pageShift,TranslateTransform.XProperty,0,140);GlassMotion.To(PageContent,OpacityProperty,1,140); }
    }
    private static string SearchText(DependencyObject item)
    {
        string value=item is TextBlock text ? text.Text : item is ContentControl content && content.Content is string label ? label : "";
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++) value+=" "+SearchText(VisualTreeHelper.GetChild(item,i));
        if(item is Border { Child: not null } border && VisualTreeHelper.GetChildrenCount(item)==0) value+=" "+SearchText(border.Child);
        if(item is Panel panel && VisualTreeHelper.GetChildrenCount(item)==0) foreach(UIElement child in panel.Children) value+=" "+SearchText(child);
        return value;
    }
    private void ResetSection(string? category = null)
    {
        var p=app.Preferences; var defaults=new Preferences(); string page=category ?? SectionNavigation.SelectedItem as string ?? "";
        ChangeModules(page switch
        {
            "外观与动画"=>p with {Opacity=defaults.Opacity,Radius=defaults.Radius,Blur=defaults.Blur,TaskbarColor=defaults.TaskbarColor,AnimationEnabled=true,StyleStartMenu=true},
            "任务栏"=>p with {WorkStart=defaults.WorkStart,WorkEnd=defaults.WorkEnd},
            "启动台"=>p with {LauncherEnabled=true,LauncherFullscreen=false}, "控制中心"=>p with {ControlCenterEnabled=true}, "状态栏"=>p with {ShowStatusBar=true,ClockSeconds=false,LunarDate=true,StatusMonitor="primary",StatusComponents=defaults.StatusComponents},
            "通知"=>p with {ReadWeChatUnread=false},
            "快捷键"=>p with {LauncherHotkey=defaults.LauncherHotkey}, "性能"=>p with {PerformancePreset="均衡"}, _=>p
        });
        bool previousReady=ready;ready=false;
        try { OpacitySlider.Value=app.Preferences.Opacity; RadiusSlider.Value=app.Preferences.Radius; BlurSlider.Value=app.Preferences.Blur; WeChatSummaryCheck.IsChecked=app.Preferences.ReadWeChatUnread;ReadColors();ReadStartMenuStatus(); }
        finally { ready=previousReady; }
    }
    private void ExportConfiguration()
    {
        var dialog=new Microsoft.Win32.SaveFileDialog { Filter="PathDeck 配置 (*.json)|*.json",FileName="PathDeck-配置.json",InitialDirectory=Preferences.Root };
        if(dialog.ShowDialog(this)!=true) return;
        try { app.ExportConfiguration(dialog.FileName); StatusText.Text="配置已导出。"; } catch(Exception error) { StatusText.Text=error.Message; }
    }
    private void ImportConfiguration()
    {
        var dialog=new Microsoft.Win32.OpenFileDialog { Filter="PathDeck 配置 (*.json)|*.json",InitialDirectory=Preferences.Root };
        if(dialog.ShowDialog(this)!=true) return;
        try
        {
            app.ImportConfiguration(dialog.FileName);ReadModules();bool previousReady=ready;ready=false;
            try {OpacitySlider.Value=app.Preferences.Opacity;RadiusSlider.Value=app.Preferences.Radius;BlurSlider.Value=app.Preferences.Blur;ReadColors();ReadStartMenuStatus();WeChatSummaryCheck.IsChecked=app.Preferences.ReadWeChatUnread;}
            finally {ready=previousReady;}
            StatusText.Text="配置已导入。";
        }
        catch(Exception error) { StatusText.Text="未能导入："+error.Message; }
    }
}
