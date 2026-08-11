using System.Windows;
using System.Windows.Input;
using CustomImageViewer.Services;

namespace CustomImageViewer;

public partial class TagCreationWindow : Window
{
    public IReadOnlyList<string> Tags { get; private set; } = [];

    public TagCreationWindow(int? targetCount = null)
    {
        InitializeComponent();
        TitleText.Text = targetCount is null
            ? "새 태그 추가"
            : $"작업물 {targetCount:N0}개에 새 태그 추가";
        Loaded += (_, _) => TagTextBox.Focus();
    }

    private void TagTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Complete();
        e.Handled = true;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        Tags = TagStore.ParseTags(TagTextBox.Text);
        if (Tags.Count == 0)
        {
            MessageBox.Show(this, "추가할 태그를 입력하세요.", "태그 추가",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
