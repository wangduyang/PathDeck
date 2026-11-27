using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickFileDesk;

public partial class HelpWindow : Window
{
    private sealed record Topic(string Title, string Summary, string[] Steps, string Note = "");
    private static readonly Topic[] Guide =
    [
        new("开始使用", "先熟悉位置、标签页和文件列表，再把常用目录固定下来。",
        [
            "双击桌面上的 PathDeck 快捷方式，或完整解压便携包后运行 PathDeck.exe。程序目录需要可写，以便保存个人设置。",
            "单击左侧的桌面、下载、文档、图片或盘符进入目录。驱动器旁的电池形进度条与百分比表示磁盘已用容量。",
            "点击标签页旁的“＋”，新标签页会打开“此电脑”。双击驱动器继续浏览；点击其他标签页可回到之前的目录。绿色圆点表示当前标签页。",
            "点击地址栏中的任意一段可返回该层；点击旁边的小箭头选择子文件夹。按 Ctrl+L 可直接输入完整本地路径或网络共享路径，再按回车。",
            "右键系统托盘的 PathDeck 图标，打开“桌面与任务栏设置…”→“外观与动画”，调整共享颜色、不透明度与模糊强度。文件管理、任务栏、通知和桌面分区会同步外观。"
        ], "建议先打开一个项目目录、一个数据目录，各保留一个标签页，体验目录之间的快速切换。"),
        new("固定路径与浏览", "把常用位置变成快捷入口，减少逐层查找。",
        [
            "进入需要固定的文件夹，点击左下角“经常访问”旁的“＋”。也可以右键一个文件夹，选择“固定到经常访问”。",
            "右键左下角的固定项，可以修改显示标签、恢复默认标签或取消固定。每条固定项同时显示名称与完整路径。",
            "点击固定项即可在当前标签页打开。新标签页的“此电脑”中也会显示同一组快捷入口。",
            "用“排序”选择名称、修改日期、修改于、类型或大小，以及升降序；用“视图”切换详细信息、仅名称、缩略图与图标大小。",
            "“修改于”用相对时间和底色区分新旧文件。文件改动会自动刷新；需要主动检查时按 F5。勾选“隐藏文件”可显示隐藏项目。"
        ], "固定到“经常访问”用于导航，不会自动把这个目录加入 Windows Search 索引。"),
        new("文件操作与终端", "文件上的右键操作针对选中项目；列表右侧和底部空白处用于当前目录。",
        [
            "单击选择文件，按 Ctrl 逐项多选，按 Shift 连续多选。Ctrl+C 复制、Ctrl+X 剪切，进入目标目录后按 Ctrl+V 粘贴。",
            "选中项目后按 F2 重命名；也可以选中后稍停，再单击名称进行编辑。按回车确认，按 Esc 取消。",
            "在列表空白处右键新建文件夹或文本文档。右键文件可复制路径、查看属性或添加颜色标签；多选后也可统一设置标签。",
            "进入 Python 工程目录，在列表空白处右键选择“在 PyCharm 中打开此工程”。右键单个文件夹时，打开的是该文件夹。需要本机已经安装 PyCharm。",
            "在同一右键菜单选择“打开终端”，终端工作目录会定位到当前目录或右键的文件夹。未安装 Windows 终端时使用命令提示符。",
            "选中项目按 Delete 或右键“删除…”。优先移到回收站；若 Windows 提示永久删除，请确认后果再继续。大目录处理时，左下角显示当前项目、动态进度与已用时间，结束后显示结果。"
        ], "文件夹被其他软件占用时，先关闭占用程序再重试。左下角动态进度用于表示任务仍在处理，不是精确的删除百分比。"),
        new("目录与跨盘搜索", "先明确搜索范围：当前目录用于项目内查找，跨盘搜索用于已索引的位置。",
        [
            "查当前项目：按 Ctrl+F，把名称或关键字输入地址栏右侧的“搜索当前目录”，按回车。范围包含当前文件夹及其子文件夹；按 Esc 清除搜索。",
            "查跨盘文件：点击窗口顶部“跨盘搜索”。在搜索页输入名称，按需要选择磁盘范围与文件类型。",
            "查看搜索页的引擎名称和索引范围。Everything 已运行且可用时优先使用它；未安装、未运行或不可用时自动尝试 Windows Search。",
            "单击结果后点“打开所在文件夹”定位到该文件；双击结果或按回车可直接打开。搜索范围只包含当前引擎已经建立索引的位置。",
            "如果 Windows Search 找不到新加入的项目，在 Windows 的“索引选项 → 修改”中勾选该目录，确认后等待系统完成索引，再回到 PathDeck 重搜。"
        ], "网络共享是否能被跨盘搜索找到，取决于所选引擎的索引配置。临时未索引的目录可直接进入后使用“搜索当前目录”。"),
        new("图片预览与大图", "先用右侧预览定位，再打开大图检查细节。",
        [
            "在文件列表中单击一张图片，右侧自动显示预览。拖动预览与列表之间的短竖线调整宽度。选择非图片或多项时，预览收起。",
            "把鼠标放到图像上，查看原图坐标与灰度或 RGB 值。坐标从原图左上角 (0, 0) 起算；解码数据较大时，右侧预览可能仅显示坐标。",
            "双击图片打开大图。点左右两侧的按钮，或按 ←、→，按打开时 PathDeck 列表的排序和筛选切换图片。",
            "滚动滚轮，以鼠标位置为中心放大或缩小。选择“手形”并按住左键拖动，或按住鼠标中键拖动，查看放大后的局部。",
            "点击“适应”让图像完整显示；点击“1:1”按像素查看。大图底部显示原图尺寸、坐标与像素读数。",
            "要用第三方软件查看，在大图上右键选择“打开方式…”，再选择本机程序。按 Esc 可关闭大图窗口。"
        ], "常用格式包括 PNG、JPEG、BMP、GIF、TIFF、ICO，实际解码能力取决于 Windows 编解码器。外部看图软件的翻页顺序由该软件决定。"),
        new("多窗口对比", "在相同像素位置对比原图、算法输出或不同版本的结果。",
        [
            "先双击打开第一张图，回到 PathDeck 再双击第二张图。把两个大图窗口放在屏幕两侧；也可继续打开更多窗口。",
            "确认所有已打开大图的宽度和高度像素数一致。任意一张尺寸不同，整组缩放、平移和同步指针都会暂停。",
            "在任意一个窗口上滚轮缩放、拖动平移，或点击“适应”“1:1”，其余符合条件的窗口会同步到对应位置。",
            "把鼠标移动到图像上，其他窗口的相同图像坐标会出现同步手形指针。移出图像后，指针消失。",
            "关闭尺寸不一致的窗口，或切换回相同尺寸的图片，联动会自动恢复。左右切图和绘制标注仍在各自窗口中操作。"
        ], "联动按图像像素尺寸判断。尺寸相同不代表内容已经配准；对比算法结果时，应先确认图像使用了相同坐标系。"),
        new("绘图、测距与保存", "在大图中记录观察结果，测量像素距离，并导出带标注的副本。",
        [
            "在大图顶部选择“画笔”自由绘制，或选择直线、箭头、矩形、椭圆，再在图像上按住左键拖动。",
            "点击“颜色”用色谱选色，或输入 HEX、RGB；也可以选择当前查看器最近使用的颜色。“线宽”以原图像素为单位。",
            "选择“测距”，从起点拖到终点。底部显示两点直线距离以及横纵像素差；矩形、椭圆会显示包围范围和像素宽高。",
            "操作有误时按 Ctrl+Z 撤销，按 Ctrl+Y 重做。切换到另一张图再返回，当前查看窗口里的标注仍会保留。若原图或标签被外部修改，会提示保留旧图和标注，或重新加载并放弃旧标注；保留旧图时，导出也使用旧图像素。",
            "点击“另存标注”或按 Ctrl+S，选择目录、文件名并保存。输出是将原图与标注合成的全尺寸 8 位彩色 PNG 副本。",
            "关闭窗口前留意未保存提示。要保留工作，先取消关闭并另存；确认放弃后，未导出的标注会丢失。"
        ], "测距单位是像素，并非毫米。此处绘制的是图像检查标注，不会修改 NIfTI 的分割标签；超大图像可能无法导出全尺寸副本。"),
        new("NIfTI 与多类分割", "同名原图与标签在同一目录配对，快速检查算法分割结果。",
        [
            "把原图与标注放在同一个目录。例如 image_001.nii 配 image_001_seg.nii.gz 或 image_001_mask.nii.gz；也可使用同名 image_001.nii 与 image_001.nii.gz。",
            "单击原图、标注或同名 PNG，右侧会显示可配对的数据叠加预览。双击其中一项，进入大图查看这一组数据。",
            "在大图的“标注不透明度”拖动滑块：0% 只看原图，50% 对照边界，100% 查看实色叠加。",
            "点击“显示类别”，按标签编号分别勾选或隐藏分割区域。一张标签图可以包含多个类别，标签 0 表示背景。",
            "把鼠标移到目标位置，通过像素读数检查原图灰度和标签值。配合滚轮放大、测距与多窗口联动，比较边界和不同算法的输出。",
            "按左右方向键或两侧按钮，按当前文件列表顺序切换数据组。换图后先检查状态提示，确认本组标签已经配对。"
        ], "当前支持单层 NIfTI-1（.nii / .nii.gz），不是完整的 DICOM 序列或三维切片查看器。原图和标签尺寸、空间信息不一致或无法确认单位时会停用叠加并提示；原图的压缩副本不会当作分割标签。"),
        new("压缩、解压与设置", "任务状态统一放在左下角，个人偏好从设置管理。",
        [
            "选中一个或多个文件或文件夹，右键“压缩为”，选择格式、输入名称。完成后压缩包生成在当前目录。",
            "右键一个支持的压缩包，选择“解压到同名文件夹”。结果生成在压缩包旁；遇到同名目录时会使用“副本”后缀。",
            "压缩、解压时查看左下角的进度和状态；需要中止时点“取消”。任务完成后以状态区的结果为准。删除任务也会在这里显示处理情况和计时。",
            "点击“设置 → 自定义右键菜单”，勾选想保留的选项并保存；“恢复默认”可重新显示全部项目。",
            "托盘菜单的“开机启动”用于登录后启动 PathDeck；文件管理设置中的“用 Win+E 打开 PathDeck”开启后，关闭主窗口仍会留在后台响应快捷键。在文件管理设置中关闭即可恢复。",
            "更新便携版前备份应用目录内的 pins.json、colors.json、menu.json、appearance.json、win-e.enabled，以及 Modules/Desktop/settings.json 和已有的 Modules/Desktop/Data。退出程序后替换程序文件，保留这些个人配置。"
        ], "ZIP、7z、TAR 及常用 TAR 复合格式可以创建和解压；创建 RAR 需要 WinRAR。密码和分卷归档请交给相应外部软件处理。"),
        new("常用快捷键", "文件列表、地址栏与大图窗口使用各自的快捷键。",
        [
            "标签页：Ctrl+T 新建“此电脑”；Ctrl+W 关闭当前标签页（至少保留一个浏览页）。",
            "定位和搜索：Ctrl+L 或 Alt+D 输入路径；Ctrl+F 搜索当前目录；F5 刷新。",
            "目录导航：Alt+← 后退；Alt+→ 前进；Alt+↑ 或非输入状态下的 Backspace 返回上级。",
            "文件整理：Ctrl+A 全选；Ctrl+C / Ctrl+X / Ctrl+V 复制、剪切、粘贴；F2 重命名；Delete 移到回收站。",
            "其他操作：Ctrl+Shift+N 新建文件夹；Ctrl+Shift+C 复制路径；Alt+Enter 查看属性。",
            "大图查看：← / → 上一张、下一张；滚轮缩放；中键拖动平移；Esc 取消当前绘制手势或关闭窗口。",
            "大图标注：Ctrl+Z 撤销；Ctrl+Y 重做；Ctrl+S 另存带标注的 PNG。调节标注不透明度时，左右键优先调节滑块。"
        ], "输入框获得焦点时，文字编辑优先；部分文件操作快捷键会暂停，避免误操作。")
    ];

    private sealed record DemoFrame(string Image, string Caption);
    private readonly DispatcherTimer demoTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DemoFrame[] frames = [];
    private int frameIndex;

    private static DemoFrame[] DemoForTopic(int topic) => topic switch
    {
        0 => [new("overview.png", "01  点击标签页旁的＋，打开“此电脑”与固定入口。"),
              new("browser.png", "02  进入目录，在同一窗口切换标签、浏览文件。"),
              new("viewer-fit.png", "03  双击图像，打开内置查看器。")],
        1 => [new("browser.png", "左下角“经常访问”的＋固定当前目录；右键固定项可以改名称。"),
              new("overview.png", "新建标签页中也会显示同一组固定入口。")],
        2 => [new("menu.png", "在文件或空白区域右键，可找到文件操作、PyCharm 与终端入口。")],
        3 => [new("browser.png", "地址栏右侧搜索当前目录；右上角“跨盘搜索”打开索引搜索页。")],
        4 or 5 => [new("browser.png", "01  单击图像在右侧预览，双击打开大图。"),
                   new("viewer-fit.png", "02  用“适应”查看全图，两侧按钮按文件列表顺序切图。"),
                   new("viewer-zoom.png", "03  放大后检查局部。同尺寸的多个查看器会同步缩放与平移。")],
        6 => [new("viewer-fit.png", "01  顶部选择画笔、形状或测距；线宽的单位是原图像素。"),
              new("colors.png", "02  点击“颜色”，从色谱选色或输入 HEX / RGB。")],
        7 => [new("mask-source.png", "01  不透明度 0%：先检查原图。"),
              new("mask-overlay.png", "02  不透明度 50%：同时检查原图与三个分割类别的边界。"),
              new("mask-labels.png", "03  不透明度 100%：查看实色叠加；“显示类别”可以分别开关各类。")],
        8 => [new("menu.png", "01  右键选择压缩或解压，执行状态显示在主界面左下角。"),
              new("settings.png", "02  配图为 1.0.7。当前右上角齿轮调整文件功能；共享外观从托盘“桌面与任务栏设置…”进入。")],
        _ => [new("browser.png", "主窗口中使用文件与目录快捷键；大图窗口中使用翻图和标注快捷键。")]
    };
    public HelpWindow()
    {
        InitializeComponent();
        demoTimer.Tick += (_, _) => ShowFrame(frameIndex + 1);
        Closed += (_, _) => demoTimer.Stop();
        Topics.ItemsSource = Guide;
        Topics.SelectedIndex = 0;
    }

    private void SelectTopic(object sender, SelectionChangedEventArgs e)
    {
        if (Topics.SelectedItem is not Topic topic) return;
        StopDemo();
        frames = DemoForTopic(Topics.SelectedIndex);
        ShowFrame(0);
        TopicTitle.Text = topic.Title;
        TopicSummary.Text = topic.Summary;
        Steps.ItemsSource = topic.Steps.Select((text, index) => new { Number = $"{index + 1:00}", Text = text });
        TopicNote.Text = topic.Note;
        NotePanel.Visibility = string.IsNullOrEmpty(topic.Note) ? Visibility.Collapsed : Visibility.Visible;
        ArticleScroll.ScrollToTop();
    }

    private void ShowFrame(int index)
    {
        if (frames.Length == 0) return;
        frameIndex = (index + frames.Length) % frames.Length;
        var frame = frames[frameIndex];
        DemoImage.Source = new BitmapImage(new Uri($"pack://application:,,,/PathDeck;component/HelpImages/{frame.Image}"));
        DemoCaption.Text = frame.Caption;
        DemoPosition.Text = $"{frameIndex + 1} / {frames.Length}";
        PlayDemoButton.IsEnabled = PreviousFrameButton.IsEnabled = NextFrameButton.IsEnabled = frames.Length > 1;
        if (SystemParameters.ClientAreaAnimation)
            DemoImage.BeginAnimation(OpacityProperty, new DoubleAnimation(.4, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void StopDemo()
    {
        demoTimer.Stop();
        PlayDemoButton.Content = "播放演示";
    }

    private void PlayDemo(object sender, RoutedEventArgs e)
    {
        if (demoTimer.IsEnabled) StopDemo();
        else { demoTimer.Start(); PlayDemoButton.Content = "暂停演示"; }
    }

    private void PreviousFrame(object sender, RoutedEventArgs e) { StopDemo(); ShowFrame(frameIndex - 1); }
    private void NextFrame(object sender, RoutedEventArgs e) { StopDemo(); ShowFrame(frameIndex + 1); }

    private void ToggleDemo(object sender, RoutedEventArgs e)
    {
        StopDemo();
        var visible = DemoContent.Visibility != Visibility.Visible;
        DemoContent.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ToggleDemoButton.Content = visible ? "收起配图" : "展开配图";
    }

    private void OpenDemoImage(object sender, MouseButtonEventArgs e)
    {
        StopDemo();
        var close = new Button { Content="关闭大图", IsCancel=true, Style=(Style)FindResource("DialogButton"),
            HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,0,0,12) };
        DockPanel.SetDock(close, Dock.Top);
        var panel = new DockPanel { Margin=new Thickness(16) };
        panel.Children.Add(close);
        panel.Children.Add(new Image { Source=DemoImage.Source, Stretch=System.Windows.Media.Stretch.Uniform });
        var image = new Window
        {
            Title=frames[frameIndex].Caption, Owner=this, Style=(Style)FindResource("DialogWindow"),
            Width=Math.Min(1200, SystemParameters.WorkArea.Width - 60),
            Height=Math.Min(820, SystemParameters.WorkArea.Height - 60),
            SizeToContent=SizeToContent.Manual, ResizeMode=ResizeMode.CanResize,
            WindowStartupLocation=WindowStartupLocation.CenterOwner, Content=panel
        };
        (Application.Current as App)?.TrackWindowTheme(image);
        image.ShowDialog();
    }
}
