using System.Windows;
using CustomImageViewer.Services;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace CustomImageViewer;

public partial class SettingsWindow : Window
{
    private readonly ThumbnailCacheStore _thumbnailCacheStore;
    private readonly TagStore _tagStore;
    private readonly AppSettings _settings;
    public int ExplorerPageSize { get; private set; }
    public int MouseWheelSpeedMultiplier { get; private set; }
    public int ExplorerSortField { get; private set; }
    public bool ExplorerSortDescending { get; private set; }
    public bool ExplorerFoldersFirst { get; private set; }
    public bool ExitOnEscape { get; private set; }
    public string TargetLanguageCode { get; private set; } = "ko";
    public int ThumbnailCacheMaxMegabytes { get; private set; }
    public bool TagAutoBackupEnabled { get; private set; }
    public int TagBackupRetentionCount { get; private set; }
    public bool TagDataChanged { get; private set; }

    public SettingsWindow(
        AppSettings settings,
        ThumbnailCacheStore thumbnailCacheStore,
        TagStore tagStore)
    {
        _settings = settings;
        _thumbnailCacheStore = thumbnailCacheStore;
        _tagStore = tagStore;
        InitializeComponent();
        PageSizeBox.Text = Math.Clamp(settings.ExplorerPageSize, 100, 1000).ToString();
        WheelSpeedBox.SelectedIndex = Math.Clamp(settings.MouseWheelSpeedMultiplier, 1, 5) - 1;
        SortFieldBox.SelectedIndex = Math.Clamp(settings.ExplorerSortField, 0, 4);
        SortDirectionBox.SelectedIndex = settings.ExplorerSortDescending ? 1 : 0;
        FoldersFirstCheckBox.IsChecked = settings.ExplorerFoldersFirst;
        ExitOnEscapeCheckBox.IsChecked = settings.ExitOnEscape;
        TargetLanguageBox.SelectedValue = settings.TargetLanguageCode;
        if (TargetLanguageBox.SelectedIndex < 0) TargetLanguageBox.SelectedValue = "ko";
        CacheSizeBox.Text = Math.Clamp(settings.ThumbnailCacheMaxMegabytes, 128, 10240).ToString();
        TagAutoBackupCheckBox.IsChecked = settings.TagAutoBackupEnabled;
        TagBackupRetentionBox.Text = Math.Clamp(settings.TagBackupRetentionCount, 3, 100).ToString();
        Loaded += async (_, _) => await RefreshCacheStatusAsync();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PageSizeBox.Text.Trim(), out var pageSize) || pageSize is < 100 or > 1000)
        {
            MessageBox.Show(this, "페이지당 항목 수는 100에서 1,000 사이로 입력하세요.", "설정",
                MessageBoxButton.OK, MessageBoxImage.Information);
            PageSizeBox.Focus();
            return;
        }

        if (!int.TryParse(CacheSizeBox.Text.Trim(), out var cacheSize) || cacheSize is < 128 or > 10240)
        {
            MessageBox.Show(this, "미리보기 캐시 용량은 128MB에서 10,240MB 사이로 입력하세요.", "설정",
                MessageBoxButton.OK, MessageBoxImage.Information);
            CacheSizeBox.Focus();
            return;
        }

        if (!int.TryParse(TagBackupRetentionBox.Text.Trim(), out var retentionCount)
            || retentionCount is < 3 or > 100)
        {
            MessageBox.Show(this, "자동 태그 백업 보관 개수는 3개에서 100개 사이로 입력하세요.", "설정",
                MessageBoxButton.OK, MessageBoxImage.Information);
            TagBackupRetentionBox.Focus();
            return;
        }

        ExplorerPageSize = pageSize;
        MouseWheelSpeedMultiplier = Math.Clamp(WheelSpeedBox.SelectedIndex + 1, 1, 5);
        ExplorerSortField = Math.Clamp(SortFieldBox.SelectedIndex, 0, 4);
        ExplorerSortDescending = SortDirectionBox.SelectedIndex == 1;
        ExplorerFoldersFirst = FoldersFirstCheckBox.IsChecked == true;
        ExitOnEscape = ExitOnEscapeCheckBox.IsChecked == true;
        TargetLanguageCode = TargetLanguageBox.SelectedValue?.ToString() ?? "ko";
        ThumbnailCacheMaxMegabytes = cacheSize;
        TagAutoBackupEnabled = TagAutoBackupCheckBox.IsChecked == true;
        TagBackupRetentionCount = retentionCount;
        DialogResult = true;
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "저장된 미리보기를 모두 삭제할까요? 원본 이미지와 태그는 삭제되지 않습니다.",
            "미리보기 캐시 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        ClearCacheButton.IsEnabled = false;
        CacheStatusText.Text = "삭제하는 중…";
        try
        {
            await _thumbnailCacheStore.ClearAsync();
            CacheStatusText.Text = "캐시를 삭제했습니다.";
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = "삭제하지 못했습니다.";
            MessageBox.Show(this, $"캐시를 삭제할 수 없습니다.\n\n{ex.Message}", "미리보기 캐시",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }

    private async Task RefreshCacheStatusAsync()
    {
        try
        {
            var stats = await _thumbnailCacheStore.GetStatsAsync();
            CacheStatusText.Text = $"{stats.Count:N0}개 · {FormatBytes(stats.SizeBytes)}";
        }
        catch
        {
            CacheStatusText.Text = "캐시 정보를 확인할 수 없습니다.";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):N1}GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):N1}MB";
        return $"{bytes / 1024d:N1}KB";
    }

    private async void BackupTags_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "태그 데이터 백업",
            Filter = "태그 데이터베이스 (*.db)|*.db",
            FileName = $"tags-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db",
            AddExtension = true,
            DefaultExt = ".db"
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunTagOperationAsync("백업하는 중…", async () =>
        {
            await _tagStore.BackupToAsync(dialog.FileName);
            TagDataStatusText.Text = $"백업 완료: {dialog.FileName}";
        });
    }

    private async void RestoreTags_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "태그 백업 복원",
            Filter = "태그 데이터베이스 (*.db)|*.db",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "현재 태그를 선택한 백업 내용으로 교체할까요?\n현재 데이터는 먼저 안전 백업됩니다.",
                "태그 복원", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await RunTagOperationAsync("복원하는 중…", async () =>
        {
            var safety = await _tagStore.CreateSafetyBackupAsync("before-restore");
            await _tagStore.RestoreFromAsync(dialog.FileName);
            TagDataChanged = true;
            TagDataStatusText.Text = $"복원 완료 · 기존 데이터 안전 백업: {safety}";
        });
    }

    private async void ExportTags_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "태그 내보내기",
            Filter = "JSON 파일 (*.json)|*.json|CSV 파일 (*.csv)|*.csv",
            FileName = $"tags-export-{DateTime.Now:yyyyMMdd}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunTagOperationAsync("내보내는 중…", async () =>
        {
            await _tagStore.ExportAsync(dialog.FileName);
            TagDataStatusText.Text = $"내보내기 완료: {dialog.FileName}";
        });
    }

    private async void RemapTagPaths_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TagPathRemapWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunTagOperationAsync("태그 경로를 변경하는 중…", async () =>
        {
            var safety = await _tagStore.CreateSafetyBackupAsync("before-remap");
            var count = await _tagStore.RemapPathsAsync(dialog.OldRootPath, dialog.NewRootPath);
            TagDataChanged = count > 0;
            TagDataStatusText.Text = $"{count:N0}개 경로 변경 완료 · 안전 백업: {safety}";
        });
    }

    private async void ResetTags_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "모든 파일과 폴더의 태그를 초기화할까요?\n이미지 원본은 삭제되지 않으며, 태그는 먼저 안전 백업됩니다.",
                "모든 태그 리셋", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await RunTagOperationAsync("안전 백업 후 태그를 초기화하는 중…", async () =>
        {
            var safety = await _tagStore.CreateSafetyBackupAsync("before-reset");
            await _tagStore.ResetAsync();
            TagDataChanged = true;
            TagDataStatusText.Text = $"모든 태그를 초기화했습니다. 안전 백업: {safety}";
        });
    }

    private async Task RunTagOperationAsync(string progressText, Func<Task> operation)
    {
        IsEnabled = false;
        TagDataStatusText.Text = progressText;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            AppLogService.Error("TagData", progressText, ex);
            TagDataStatusText.Text = "작업을 완료하지 못했습니다.";
            MessageBox.Show(this, ex.Message, "태그 데이터", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLogService.LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLogService.LogFolder}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogService.Error("Diagnostics", "로그 폴더를 열지 못했습니다.", ex);
            MessageBox.Show(this, ex.Message, "로그 폴더", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = new AppSettings
            {
                LastFolderPath = _settings.LastFolderPath,
                ExplorerPageSize = int.TryParse(PageSizeBox.Text, out var pageSize) ? pageSize : _settings.ExplorerPageSize,
                ThumbnailCacheMaxMegabytes = int.TryParse(CacheSizeBox.Text, out var cacheSize) ? cacheSize : _settings.ThumbnailCacheMaxMegabytes,
                MouseWheelSpeedMultiplier = Math.Clamp(WheelSpeedBox.SelectedIndex + 1, 1, 5),
                ExplorerSortField = Math.Max(0, SortFieldBox.SelectedIndex),
                ExplorerSortDescending = SortDirectionBox.SelectedIndex == 1,
                ExplorerFoldersFirst = FoldersFirstCheckBox.IsChecked == true,
                TargetLanguageCode = TargetLanguageBox.SelectedValue?.ToString() ?? "ko",
                TagAutoBackupEnabled = TagAutoBackupCheckBox.IsChecked == true,
                TagBackupRetentionCount = int.TryParse(TagBackupRetentionBox.Text, out var retention) ? retention : 10
            };
            Clipboard.SetText(AppLogService.CreateDiagnosticReport(snapshot));
            TagDataStatusText.Text = "진단 정보를 클립보드에 복사했습니다.";
        }
        catch (Exception ex)
        {
            AppLogService.Error("Diagnostics", "진단 정보를 복사하지 못했습니다.", ex);
            MessageBox.Show(this, ex.Message, "진단 정보", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
