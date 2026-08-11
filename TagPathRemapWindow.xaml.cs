using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace CustomImageViewer;

public partial class TagPathRemapWindow : Window
{
    public string OldRootPath { get; private set; } = string.Empty;
    public string NewRootPath { get; private set; } = string.Empty;

    public TagPathRemapWindow() => InitializeComponent();

    private void BrowseOld_Click(object sender, RoutedEventArgs e) => BrowseInto(OldRootBox);
    private void BrowseNew_Click(object sender, RoutedEventArgs e) => BrowseInto(NewRootBox);

    private void BrowseInto(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "폴더 선택" };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var oldRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OldRootBox.Text.Trim()));
            var newRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NewRootBox.Text.Trim()));
            if (!Directory.Exists(newRoot))
            {
                MessageBox.Show(this, "새 루트 폴더가 존재하지 않습니다.", "태그 경로 변경",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "기존 루트와 새 루트가 같습니다.", "태그 경로 변경",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OldRootPath = oldRoot;
            NewRootPath = newRoot;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, "올바른 폴더 경로를 입력하세요.", "태그 경로 변경",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
