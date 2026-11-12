using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace QuickFileDesk;

public partial class AnnotationSaveWindow : Window
{
    internal string OutputPath { get; private set; } = "";

    internal AnnotationSaveWindow(string source)
    {
        InitializeComponent();
        FolderInput.Text = Path.GetDirectoryName(source) ?? "";
        NameInput.Text = Path.GetFileNameWithoutExtension(source) + "-标注";
    }

    private void FocusName(object sender, RoutedEventArgs e)
    {
        NameInput.Focus();
        NameInput.SelectAll();
    }

    private void InputChanged(object sender, TextChangedEventArgs e)
    {
        if (InputError is not null) InputError.Visibility = Visibility.Collapsed;
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择标注副本的保存位置" };
        if (Directory.Exists(FolderInput.Text)) picker.InitialDirectory = FolderInput.Text;
        if (picker.ShowDialog(this) == true) FolderInput.Text = picker.FolderName;
    }

    private void AcceptPath(object sender, RoutedEventArgs e)
    {
        var folder = FolderInput.Text.Trim();
        var name = NameInput.Text.Trim();
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            ShowError("请输入有效的保存位置，或点「浏览」选择文件夹。", FolderInput);
            return;
        }
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("请输入有效的文件名。", NameInput);
            return;
        }
        var output = Path.Combine(folder, name + ".png");
        if (File.Exists(output) || Directory.Exists(output))
        {
            ShowError("这个名称已被占用，请换一个文件名。", NameInput);
            return;
        }
        OutputPath = output;
        DialogResult = true;
    }

    private void ShowError(string message, TextBox input)
    {
        InputError.Text = message;
        InputError.Visibility = Visibility.Visible;
        input.Focus();
        input.SelectAll();
    }
}
