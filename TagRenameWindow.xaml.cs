using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CustomImageViewer;

public partial class TagRenameWindow : Window
{
    public string OriginalName => ExistingTagBox.SelectedItem?.ToString() ?? string.Empty;
    public string NewName { get; private set; } = string.Empty;

    public TagRenameWindow(IEnumerable<string> tagNames)
    {
        InitializeComponent();
        ExistingTagBox.ItemsSource = tagNames
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ExistingTagBox.SelectedIndex = ExistingTagBox.Items.Count > 0 ? 0 : -1;
        Loaded += (_, _) => NewNameBox.Focus();
    }

    private void ExistingTagBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        NewNameBox.Text = OriginalName;
        NewNameBox.SelectAll();
    }

    private void NewNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Complete();
        e.Handled = true;
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        NewName = NewNameBox.Text.Trim().TrimStart('#').Trim();
        if (string.IsNullOrWhiteSpace(OriginalName) || string.IsNullOrWhiteSpace(NewName))
        {
            MessageBox.Show(this, "기존 태그와 새 이름을 모두 지정하세요.", "태그 수정하기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
