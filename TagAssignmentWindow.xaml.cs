using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CustomImageViewer.Services;

namespace CustomImageViewer;

public partial class TagAssignmentWindow : Window
{
    private readonly HashSet<string> _selectedTags = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> _availableTags = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly TagSelectionPurpose _purpose;
    private static readonly Color[] ChipColors =
    [
        Color.FromRgb(66, 133, 244), Color.FromRgb(171, 71, 188),
        Color.FromRgb(0, 137, 123), Color.FromRgb(239, 108, 0),
        Color.FromRgb(57, 73, 171), Color.FromRgb(216, 27, 96),
        Color.FromRgb(67, 160, 71), Color.FromRgb(0, 137, 173)
    ];

    public IReadOnlyList<string> SelectedTags => _selectedTags
        .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public TagAssignmentWindow(
        IEnumerable<TagSummary> existingTags,
        int targetCount,
        bool allowNewTags = true,
        TagSelectionPurpose purpose = TagSelectionPurpose.Apply)
    {
        InitializeComponent();
        _purpose = purpose;
        TitleText.Text = purpose == TagSelectionPurpose.Remove
            ? $"작업물 {targetCount:N0}개에서 태그 지우기"
            : $"작업물 {targetCount:N0}개에 기존 태그 적용";
        NewTagPanel.Visibility = allowNewTags ? Visibility.Visible : Visibility.Collapsed;
        if (purpose == TagSelectionPurpose.Remove)
        {
            InstructionText.Text = "선택한 작업물에서 지울 태그를 고르세요. 다른 작업물의 태그는 유지됩니다.";
            ApplyButton.Content = "선택한 태그 지우기";
            ApplyButton.Background = new SolidColorBrush(Color.FromRgb(132, 48, 48));
            ApplyButton.BorderBrush = new SolidColorBrush(Color.FromRgb(205, 88, 88));
        }
        else if (!allowNewTags)
            InstructionText.Text = "선택한 작업물에 적용할 기존 태그를 고르세요.";
        foreach (var tag in existingTags)
        {
            _availableTags.Add(tag.Name);
            AddTagChip(tag.Name, $"현재 {tag.UsageCount:N0}개 작업물에 사용 중");
        }
        UpdateState();
        if (allowNewTags) Loaded += (_, _) => NewTagBox.Focus();
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) => AddEnteredTags();

    private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddEnteredTags();
        e.Handled = true;
    }

    private void AddEnteredTags()
    {
        var tags = TagStore.ParseTags(NewTagBox.Text);
        foreach (var tag in tags)
        {
            if (_availableTags.Add(tag)) AddTagChip(tag, "새 태그");
            _selectedTags.Add(tag);
        }

        foreach (var chip in AvailableTagsPanel.Children.OfType<ToggleButton>())
            if (chip.Tag is string tag && _selectedTags.Contains(tag))
            {
                chip.IsChecked = true;
                UpdateChipAppearance(chip, GetChipColor(tag));
            }

        NewTagBox.Clear();
        UpdateState();
    }

    private void AddTagChip(string tagName, string toolTip)
    {
        var color = GetChipColor(tagName);
        var button = new ToggleButton
        {
            Content = tagName,
            Tag = tagName,
            ToolTip = toolTip,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(4, 3, 4, 3),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(color)
        };
        button.Click += TagChip_Click;
        UpdateChipAppearance(button, color);
        AvailableTagsPanel.Children.Add(button);
    }

    private void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tagName) return;
        if (button.IsChecked == true) _selectedTags.Add(tagName); else _selectedTags.Remove(tagName);
        UpdateChipAppearance(button, GetChipColor(tagName));
        UpdateState();
    }

    private void UpdateState()
    {
        SelectedTagCountText.Text = _purpose == TagSelectionPurpose.Remove
            ? $"지울 태그 {_selectedTags.Count:N0}개"
            : $"적용할 태그 {_selectedTags.Count:N0}개";
        ApplyButton.IsEnabled = _selectedTags.Count > 0;
    }

    private static Color GetChipColor(string tagName)
    {
        var hash = 17;
        foreach (var character in tagName.ToUpperInvariant())
            hash = unchecked(hash * 31 + character);
        return ChipColors[(hash & int.MaxValue) % ChipColors.Length];
    }

    private static void UpdateChipAppearance(ToggleButton button, Color color)
    {
        var selected = button.IsChecked == true;
        button.Background = new SolidColorBrush(selected
            ? Color.FromArgb(230, color.R, color.G, color.B)
            : Color.FromArgb(70, color.R, color.G, color.B));
        button.Opacity = selected ? 1 : 0.8;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

public enum TagSelectionPurpose { Apply, Remove }
