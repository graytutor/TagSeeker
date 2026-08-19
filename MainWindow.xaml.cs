using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CustomImageViewer.Models;
using CustomImageViewer.Services;

namespace CustomImageViewer;

public partial class MainWindow : Window
{
    private const int TranslationPrefetchImageCount = 4;
    private readonly BulkObservableCollection<ImageFileItem> _images = [];
    private readonly BulkObservableCollection<ImageFileItem> _visibleImages = [];
    private readonly BulkObservableCollection<PrefixFilterOption> _visiblePrefixOptions = [];
    private readonly List<PrefixFilterOption> _allPrefixOptions = [];
    private readonly HashSet<string> _selectedPrefixKeys = new(StringComparer.CurrentCultureIgnoreCase);
    private List<ImageFileItem> _prefixFilterSource = [];
    private readonly MagickImageDecoder _magickDecoder = new();
    private readonly IImageDecoder _decoder;
    private readonly TagStore _tagStore = new();
    private readonly WindowsOcrService _ocrService = new();
    private readonly PaddleOcrService _localOcrService = new();
    private readonly OllamaTranslatorService _translatorService = new();
    private readonly BuiltInQwenTranslatorService _builtInTranslatorService = new();
    private readonly MyMemoryTranslatorService _freeTranslatorService = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly OcrCacheStore _ocrCacheStore = new();
    private readonly ThumbnailCacheStore _thumbnailCacheStore = new();
    private readonly AppUpdateService _updateService = new();
    private readonly Dictionary<string, ImageTextCacheEntry> _imageTextCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedTagNames = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly BulkObservableCollection<TagSetSummary> _tagSets = [];
    private readonly List<string> _folderHistory = [];
    private readonly Dictionary<string, ExplorerLocationState> _folderLocations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _thumbnailDecodeGate = new(3);
    private readonly HashSet<string> _cutClipboardPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Color[] TagChipColors =
    [
        Color.FromRgb(66, 133, 244), Color.FromRgb(171, 71, 188),
        Color.FromRgb(0, 137, 123), Color.FromRgb(239, 108, 0),
        Color.FromRgb(57, 73, 171), Color.FromRgb(216, 27, 96),
        Color.FromRgb(67, 160, 71), Color.FromRgb(0, 137, 173),
        Color.FromRgb(117, 117, 117), Color.FromRgb(124, 77, 255)
    ];
    private AppSettings _settings = new();
    private CancellationTokenSource? _ocrCancellation;
    private string _lastDetectedOcrLanguage = "en";
    private OcrTextResult? _lastOcrResult;
    private IReadOnlyList<string> _translatedOverlayLines = [];
    private CancellationTokenSource? _thumbnailCancellation;
    private CancellationTokenSource? _tagFilterDebounceCancellation;
    private CancellationTokenSource? _viewerCancellation;
    private CancellationTokenSource? _folderRefreshCancellation;
    private FileSystemWatcher? _folderWatcher;
    private int _tagFilterGeneration;
    private readonly DispatcherTimer _animationTimer = new();
    private readonly DispatcherTimer _typeSearchResetTimer = new();
    private IReadOnlyList<AnimationFrame> _animationFrames = [];
    private int _animationFrameIndex;
    private string _currentFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    private int _currentIndex = -1;
    private ViewerMode _viewerMode = ViewerMode.FitIncludingSmall;
    private bool _isNavigating;
    private bool _continuousTranslationEnabled;
    private string? _continuousTranslationFolder;
    private bool _translationOverlayVisible;
    private bool _restoringCachedTextState;
    private bool _isMouseFolderNavigating;
    private bool _isAutoRefreshingFolder;
    private bool _pendingFolderRefresh;
    private bool _isRefreshingTagSets;
    private string _typeSearchBuffer = string.Empty;
    private int _folderHistoryIndex = -1;
    private int _currentExplorerPage;
    private ExplorerLocationState? _tagFilterReturnState;
    private string? _tagFilterReturnFolder;
    private int ExplorerPageSize => Math.Clamp(_settings.ExplorerPageSize, 100, 1000);
    private int MouseWheelSpeedMultiplier => Math.Clamp(_settings.MouseWheelSpeedMultiplier, 1, 5);
    private const string UnassignedPrefixKey = "\0";

    private enum ExplorerSortField
    {
        Name,
        DateModified,
        DateCreated,
        Type,
        Size
    }

    private sealed record ExplorerLocationState(
        int Page,
        double VerticalOffset,
        IReadOnlyList<string> SelectedPaths);

    public MainWindow()
    {
        _decoder = new CompositeImageDecoder(
            new TgaImageDecoder(),
            new WpfImageDecoder(),
            _magickDecoder);
        InitializeComponent();
        ThumbnailList.ItemsSource = _visibleImages;
        PrefixOptionList.ItemsSource = _visiblePrefixOptions;
        TagSetBox.ItemsSource = _tagSets;
        ViewModeBox.SelectedIndex = 0;
        _animationTimer.Tick += AnimationTimer_Tick;
        _typeSearchResetTimer.Interval = TimeSpan.FromMilliseconds(1100);
        _typeSearchResetTimer.Tick += (_, _) => ResetTypeSearch();
        Closing += (_, _) =>
        {
            DisposeFolderWatcher();
            _ocrCancellation?.Cancel();
            SaveUserSettings();
        };
        Loaded += async (_, _) =>
        {
            await _tagStore.InitializeAsync();
            _settings = await _settingsStore.LoadAsync();
            await _builtInTranslatorService.ConfigureModelFolderAsync(_settings.QwenModelFolder);
            await RefreshTagSetsAsync(_settings.ActiveTagSetId);
            // The initial folder can contain many images. Show the active tag set
            // before OCR-cache maintenance and thumbnail decoding begin.
            await RefreshTagCloudAsync();
            await _ocrCacheStore.InitializeAsync();
            await _ocrCacheStore.CleanupAsync();
            foreach (var entry in await _ocrCacheStore.LoadAllAsync())
                _imageTextCache[entry.ImagePath] = new ImageTextCacheEntry(
                    entry.FileLength, entry.LastWriteUtcTicks, entry.OcrResult,
                    entry.TranslatedText, entry.OverlayLines,
                    entry.TargetLanguageCode, entry.TranslationProvider, entry.OverlayEnabled);
            if (_settings.TagAutoBackupEnabled
                && (_settings.LastTagBackupUtcTicks <= 0
                    || new DateTime(_settings.LastTagBackupUtcTicks, DateTimeKind.Utc) < DateTime.UtcNow.AddDays(-1)))
            {
                try
                {
                    await _tagStore.CreateAutomaticBackupAsync(_settings.TagBackupRetentionCount);
                    _settings.LastTagBackupUtcTicks = DateTime.UtcNow.Ticks;
                    await _settingsStore.SaveAsync(_settings);
                }
                catch (Exception ex)
                {
                    AppLogService.Warning("TagBackup", "자동 태그 백업에 실패했습니다.", ex);
                }
            }
            await _thumbnailCacheStore.InitializeAsync();
            await _thumbnailCacheStore.CleanupAsync(_settings.ThumbnailCacheMaxMegabytes);
            ApplySavedUserSettings();
            string? startupImagePath = null;
            if (App.StartupPath is { } startupPath && Directory.Exists(startupPath))
            {
                _currentFolder = startupPath;
            }
            else if (App.StartupPath is { } startupFile && File.Exists(startupFile))
            {
                startupImagePath = startupFile;
                _currentFolder = Path.GetDirectoryName(startupFile) ?? _currentFolder;
            }
            else if (!string.IsNullOrWhiteSpace(_settings.LastFolderPath) && Directory.Exists(_settings.LastFolderPath))
            {
                _currentFolder = _settings.LastFolderPath;
            }
            await LoadFolderAsync(_currentFolder);
            if (startupImagePath is not null && _decoder.CanDecode(startupImagePath))
            {
                var startupIndex = _images.ToList().FindIndex(item =>
                    string.Equals(item.FullPath, startupImagePath, StringComparison.OrdinalIgnoreCase));
                if (startupIndex >= 0)
                    await ShowImageAsync(startupIndex);
            }
            _ = CheckForUpdatesOnStartupAsync();
        };
    }

    private void ApplySavedUserSettings()
    {
        var modeIndex = Enum.IsDefined(typeof(ViewerMode), _settings.ViewerMode)
            ? _settings.ViewerMode
            : (int)ViewerMode.FitIncludingSmall;
        _viewerMode = (ViewerMode)modeIndex;
        ViewModeBox.SelectedIndex = modeIndex;
        ExitOnEscapeCheckBox.IsChecked = _settings.ExitOnEscape;
        SortFieldBox.SelectedIndex = Enum.IsDefined(typeof(ExplorerSortField), _settings.ExplorerSortField)
            ? _settings.ExplorerSortField
            : (int)ExplorerSortField.Name;
        SortDirectionBox.SelectedIndex = _settings.ExplorerSortDescending ? 1 : 0;
        FoldersFirstCheckBox.IsChecked = _settings.ExplorerFoldersFirst;
        ThumbnailSizeSlider.Value = Math.Clamp(_settings.ExplorerThumbnailSize, 120, 320);
        ApplyThumbnailSize(ThumbnailSizeSlider.Value);
        HidePrefixCheckBox.IsChecked = _settings.HidePrefix;

        var targetExists = TargetLanguageBox.Items.OfType<ComboBoxItem>()
            .Any(item => string.Equals(item.Tag?.ToString(), _settings.TargetLanguageCode, StringComparison.OrdinalIgnoreCase));
        TargetLanguageBox.SelectedValue = targetExists ? _settings.TargetLanguageCode : "ko";

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        Width = Math.Clamp(_settings.WindowWidth, MinWidth, Math.Max(MinWidth, virtualWidth));
        Height = Math.Clamp(_settings.WindowHeight, MinHeight, Math.Max(MinHeight, virtualHeight));

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            var visibleWidth = Math.Max(0, Math.Min(left + Width, virtualLeft + virtualWidth) - Math.Max(left, virtualLeft));
            var visibleHeight = Math.Max(0, Math.Min(top + Height, virtualTop + virtualHeight) - Math.Max(top, virtualTop));
            if (visibleWidth >= 120 && visibleHeight >= 80)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveUserSettings()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.ViewerMode = (int)_viewerMode;
        _settings.TargetLanguageCode =
            (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ko";
        _settings.ExitOnEscape = ExitOnEscapeCheckBox.IsChecked == true;
        _settings.ExplorerSortField = Math.Max(0, SortFieldBox.SelectedIndex);
        _settings.ExplorerSortDescending = SortDirectionBox.SelectedIndex == 1;
        _settings.ExplorerFoldersFirst = FoldersFirstCheckBox.IsChecked == true;
        _settings.ExplorerThumbnailSize = ThumbnailSizeSlider.Value;
        _settings.HidePrefix = HidePrefixCheckBox.IsChecked == true;

        try { _settingsStore.Save(_settings); }
        catch { }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_settings.AutomaticUpdateCheckEnabled) return;
        if (_settings.LastUpdateCheckUtcTicks > 0)
        {
            try
            {
                var lastCheck = new DateTime(_settings.LastUpdateCheckUtcTicks, DateTimeKind.Utc);
                if (lastCheck > DateTime.UtcNow.AddDays(-1)) return;
            }
            catch (ArgumentOutOfRangeException)
            {
                _settings.LastUpdateCheckUtcTicks = 0;
            }
        }

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null) return;
            var answer = MessageBox.Show(this,
                $"새 TagSeeker {update.VersionText} 버전이 있습니다. 지금 업데이트할까요?\n\n" +
                "다운로드와 확인이 끝나면 프로그램이 자동으로 다시 시작됩니다.",
                "TagSeeker 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;

            StatusText.Text = $"TagSeeker {update.VersionText} 업데이트를 다운로드하는 중…";
            var progress = new Progress<double>(value =>
                StatusText.Text = value < 0.9
                    ? $"업데이트 다운로드 중… {value / 0.9:P0}"
                    : "업데이트를 확인하고 적용 준비 중…");
            await _updateService.DownloadAndPrepareAsync(update, progress);
            StatusText.Text = "업데이트 준비 완료. TagSeeker를 다시 시작합니다…";
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLogService.Warning("Update", "자동 업데이트 확인 또는 준비에 실패했습니다.", ex);
            StatusText.Text = "업데이트를 확인하지 못했습니다. 다음에 다시 확인합니다.";
        }
        finally
        {
            _settings.LastUpdateCheckUtcTicks = DateTime.UtcNow.Ticks;
            try { await _settingsStore.SaveAsync(_settings); }
            catch (Exception ex) { AppLogService.Warning("Update", "업데이트 확인 시각을 저장하지 못했습니다.", ex); }
        }
    }

    private async Task LoadFolderAsync(
        string folder,
        bool recordHistory = true,
        bool captureCurrentLocation = true,
        ExplorerLocationState? restoreLocation = null)
    {
        string normalizedFolder;
        try
        {
            normalizedFolder = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, "올바른 폴더 경로가 아닙니다.", "폴더 열기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(normalizedFolder))
        {
            MessageBox.Show(this, "폴더가 없거나 더 이상 사용할 수 없습니다.", "폴더 열기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tagFilterWasActive = ClearTagSearchButton.Visibility == Visibility.Visible;
        var folderIsChanging = !string.Equals(
            NormalizeFolderLocationKey(_currentFolder),
            NormalizeFolderLocationKey(normalizedFolder),
            StringComparison.OrdinalIgnoreCase);
        if (folderIsChanging && _continuousTranslationEnabled)
            DisableContinuousTranslation();
        if (captureCurrentLocation
            && !tagFilterWasActive
            && Directory.Exists(_currentFolder))
        {
            _folderLocations[NormalizeFolderLocationKey(_currentFolder)] = CaptureExplorerLocation();
        }
        if (folderIsChanging && tagFilterWasActive)
        {
            ClearTagSearchButton.Visibility = Visibility.Collapsed;
            _tagFilterReturnState = null;
            _tagFilterReturnFolder = null;
        }

        List<ImageFileItem> items;
        try
        {
            var folders = Directory.EnumerateDirectories(normalizedFolder)
                .Select(path => new ImageFileItem(path, isDirectory: true, isImage: false));
            var files = Directory.EnumerateFiles(normalizedFolder)
                .Select(path => new ImageFileItem(path, isDirectory: false, isImage: _decoder.CanDecode(path)));
            items = SortExplorerItems(folders.Concat(files));
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, "이 폴더를 읽을 권한이 없습니다.", "폴더 열기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"폴더를 읽는 중 문제가 발생했습니다.\n\n{ex.Message}", "폴더 열기",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _tagFilterDebounceCancellation?.Cancel();
        _tagFilterGeneration++;
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = new CancellationTokenSource();
        var token = _thumbnailCancellation.Token;
        _currentFolder = normalizedFolder;
        ResetTypeSearch();
        if (recordHistory) RecordFolderHistory(_currentFolder);
        FolderPathBox.Text = _currentFolder;
        ThumbnailList.UnselectAll();
        _settings.LastFolderPath = _currentFolder;
        await _settingsStore.SaveAsync(_settings);

        var tagsByPath = await _tagStore.GetTagsForPathsAsync(items.Select(item => item.FullPath));
        foreach (var item in items)
            if (tagsByPath.TryGetValue(item.FullPath, out var tags))
                item.TagsText = string.Join(", ", tags);
        if (restoreLocation is null)
            _folderLocations.TryGetValue(NormalizeFolderLocationKey(normalizedFolder), out restoreLocation);
        SetExplorerItems(items, restoreLocation, resetPrefixFilter: folderIsChanging);
        ConfigureFolderWatcher();

        var folderCount = items.Count(item => item.IsDirectory);
        var imageCount = items.Count(item => item.IsImage);
        var otherCount = items.Count - folderCount - imageCount;
        StatusText.Text = $"폴더 {folderCount:N0}개   이미지 {imageCount:N0}개   기타 파일 {otherCount:N0}개";

    }

    private void ConfigureFolderWatcher()
    {
        DisposeFolderWatcher();
        if (!Directory.Exists(_currentFolder)) return;

        try
        {
            _folderWatcher = new FileSystemWatcher(_currentFolder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _folderWatcher.Created += FolderWatcher_Changed;
            _folderWatcher.Deleted += FolderWatcher_Changed;
            _folderWatcher.Changed += FolderWatcher_Changed;
            _folderWatcher.Renamed += FolderWatcher_Changed;
            _folderWatcher.Error += FolderWatcher_Error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLogService.Warning("FolderWatcher", $"폴더 변경 감시를 시작하지 못했습니다: {_currentFolder}", ex);
        }
    }

    private void DisposeFolderWatcher()
    {
        _folderRefreshCancellation?.Cancel();
        _folderRefreshCancellation?.Dispose();
        _folderRefreshCancellation = null;
        if (_folderWatcher is null) return;
        _folderWatcher.EnableRaisingEvents = false;
        _folderWatcher.Dispose();
        _folderWatcher = null;
    }

    private void FolderWatcher_Changed(object sender, FileSystemEventArgs e) => ScheduleFolderRefresh();

    private void FolderWatcher_Error(object sender, ErrorEventArgs e)
    {
        AppLogService.Warning("FolderWatcher", $"폴더 변경 알림이 유실되어 목록을 다시 읽습니다: {_currentFolder}", e.GetException());
        ScheduleFolderRefresh();
    }

    private void ScheduleFolderRefresh()
    {
        _folderRefreshCancellation?.Cancel();
        _folderRefreshCancellation?.Dispose();
        var cancellation = _folderRefreshCancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(450, token);
                if (token.IsCancellationRequested) return;
                if (ViewerView.Visibility == Visibility.Visible)
                {
                    _pendingFolderRefresh = true;
                    return;
                }
                await RefreshFolderFromWatcherAsync();
            }
            catch (OperationCanceledException)
            {
                // A newer file-system event replaces this refresh request.
            }
        });
    }

    private async Task RefreshFolderFromWatcherAsync()
    {
        if (_isAutoRefreshingFolder || !Directory.Exists(_currentFolder)) return;
        _isAutoRefreshingFolder = true;
        try
        {
            if (ClearTagSearchButton.Visibility == Visibility.Visible && _selectedTagNames.Count > 0)
            {
                await ApplySelectedTagFilterAsync(++_tagFilterGeneration);
            }
            else
            {
                var location = CaptureExplorerLocation();
                await LoadFolderAsync(
                    _currentFolder,
                    recordHistory: false,
                    captureCurrentLocation: false,
                    restoreLocation: location);
            }
            StatusText.Text = "폴더 변경 사항을 자동으로 반영했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning("FolderWatcher", $"폴더 자동 새로 고침에 실패했습니다: {_currentFolder}", ex);
        }
        finally
        {
            _isAutoRefreshingFolder = false;
        }
    }

    private async Task LoadThumbnailsAsync(IEnumerable<ImageFileItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) return;
            if (item.ThumbnailLoadStarted) continue;
            item.ThumbnailLoadStarted = true;

            try
            {
                await _thumbnailDecodeGate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                BitmapSource? cachedThumbnail = null;
                try
                {
                    cachedThumbnail = await _thumbnailCacheStore.TryLoadAsync(item.FullPath, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // A cache failure must never prevent the original image from loading.
                }

                if (cachedThumbnail is not null)
                {
                    item.Thumbnail = cachedThumbnail;
                    continue;
                }

                string? previewPath = null;
                if (item.IsImage)
                {
                    previewPath = item.FullPath;
                }
                else if (item.IsDirectory)
                {
                    previewPath = await Task.Run(
                        () => TryFindFolderPreviewImage(item.FullPath, token));
                }

                if (previewPath is not null)
                {
                    var thumbnail = await _decoder.LoadAsync(previewPath, 320, token);
                    if (thumbnail is not null)
                    {
                        item.Thumbnail = thumbnail;
                        try
                        {
                            await _thumbnailCacheStore.SaveAsync(item.FullPath, previewPath, thumbnail, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch
                        {
                            // Keep the decoded thumbnail even if it could not be cached.
                        }
                    }
                    else
                        item.HasDecodeError = true;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                item.HasDecodeError = true;
            }
            finally
            {
                _thumbnailDecodeGate.Release();
            }
        }
    }

    private string? TryFindFolderPreviewImage(string folderPath, CancellationToken token)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };
            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", options))
            {
                if (token.IsCancellationRequested) return null;
                if (_decoder.CanDecode(filePath)) return filePath;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            // Protected/system folders remain visible with the normal folder placeholder.
        }
        return null;
    }

    private ExplorerLocationState CaptureExplorerLocation()
    {
        var offset = FindVisualChild<ScrollViewer>(ThumbnailList)?.VerticalOffset ?? 0;
        var selectedPaths = ThumbnailList.SelectedItems.OfType<ImageFileItem>()
            .Select(item => item.FullPath)
            .ToList();
        return new ExplorerLocationState(_currentExplorerPage, offset, selectedPaths);
    }

    private static string NormalizeFolderLocationKey(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private void SetExplorerItems(
        IEnumerable<ImageFileItem> items,
        ExplorerLocationState? restoreLocation = null,
        bool resetPrefixFilter = false)
    {
        if (resetPrefixFilter) _selectedPrefixKeys.Clear();
        _prefixFilterSource = items.ToList();
        RebuildPrefixOptions();
        ApplyPrefixDisplay(_prefixFilterSource);
        _images.ReplaceAll(FilterItemsBySelectedPrefixes(_prefixFilterSource));
        _currentExplorerPage = restoreLocation?.Page ?? 0;
        ShowExplorerPage(verticalOffset: restoreLocation?.VerticalOffset);

        if (restoreLocation is not { SelectedPaths.Count: > 0 }) return;
        var selectedPaths = restoreLocation.SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dispatcher.BeginInvoke(() =>
        {
            ThumbnailList.UnselectAll();
            foreach (var item in _visibleImages.Where(item => selectedPaths.Contains(item.FullPath)))
                ThumbnailList.SelectedItems.Add(item);
        }, DispatcherPriority.Loaded);
    }

    private void RebuildPrefixOptions()
    {
        var groups = _prefixFilterSource
            .Where(item => item.IsDirectory)
            .GroupBy(GetPrefixKey, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Name = group.Key == UnassignedPrefixKey ? "접두어 없음" : group.Key,
                Count = group.Count()
            })
            .OrderBy(group => group.Key == UnassignedPrefixKey ? 1 : 0)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var validKeys = groups.Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        _selectedPrefixKeys.RemoveWhere(key => !validKeys.Contains(key));
        _allPrefixOptions.Clear();
        foreach (var group in groups)
        {
            _allPrefixOptions.Add(new PrefixFilterOption(group.Key, group.Name, group.Count)
            {
                IsSelected = _selectedPrefixKeys.Contains(group.Key)
            });
        }
        ApplyPrefixOptionSearch();
        UpdatePrefixFilterSummary();
    }

    private string GetPrefixKey(ImageFileItem item) =>
        TryExtractLeadingPrefix(item.FileName, out var prefix, out _) ? prefix : UnassignedPrefixKey;

    private bool TryExtractLeadingPrefix(string name, out string prefix, out int prefixLength)
    {
        prefix = string.Empty;
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var leadingWhitespace = name.Length - name.TrimStart().Length;
        var trimmed = name[leadingWhitespace..];
        if (trimmed.Length < 3) return false;

        var patterns = _settings.PrefixPatterns ?? [];
        foreach (var pattern in patterns
                     .Where(pattern => !string.IsNullOrEmpty(pattern.Opening)
                                       && !string.IsNullOrEmpty(pattern.Closing))
                     .OrderByDescending(pattern => pattern.Opening.Length))
        {
            if (!trimmed.StartsWith(pattern.Opening, StringComparison.Ordinal)) continue;

            var closingIndex = trimmed.IndexOf(
                pattern.Closing,
                pattern.Opening.Length,
                StringComparison.Ordinal);
            if (closingIndex <= pattern.Opening.Length) continue;

            var inner = trimmed.Substring(
                pattern.Opening.Length,
                closingIndex - pattern.Opening.Length);
            if (string.IsNullOrWhiteSpace(inner)) continue;

            var wrappedLength = closingIndex + pattern.Closing.Length;
            prefix = trimmed[..wrappedLength];
            prefixLength = leadingWhitespace + wrappedLength;
            return true;
        }

        return false;
    }

    private IEnumerable<ImageFileItem> FilterItemsBySelectedPrefixes(IEnumerable<ImageFileItem> source) =>
        _selectedPrefixKeys.Count == 0
            ? source
            : source.Where(item => item.IsDirectory && _selectedPrefixKeys.Contains(GetPrefixKey(item)));

    private void ApplyPrefixFilter()
    {
        var selectedPaths = ThumbnailList.SelectedItems.OfType<ImageFileItem>()
            .Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = FilterItemsBySelectedPrefixes(_prefixFilterSource).ToList();
        _images.ReplaceAll(filtered);

        var firstSelectedIndex = filtered.FindIndex(item => selectedPaths.Contains(item.FullPath));
        _currentExplorerPage = firstSelectedIndex >= 0 ? firstSelectedIndex / ExplorerPageSize : 0;
        ShowExplorerPage();
        if (selectedPaths.Count > 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ThumbnailList.UnselectAll();
                foreach (var item in _visibleImages.Where(item => selectedPaths.Contains(item.FullPath)))
                    ThumbnailList.SelectedItems.Add(item);
                if (ThumbnailList.SelectedItem is ImageFileItem selected)
                    ThumbnailList.ScrollIntoView(selected);
            }, DispatcherPriority.Loaded);
        }
        UpdatePrefixFilterSummary();
        StatusText.Text = _selectedPrefixKeys.Count == 0
            ? $"접두어 필터 해제 · 전체 {_images.Count:N0}개"
            : $"접두어 필터 결과 {_images.Count:N0}개";
    }

    private void ApplyPrefixDisplay(IEnumerable<ImageFileItem> items)
    {
        var hidePrefix = HidePrefixCheckBox.IsChecked == true;
        foreach (var item in items)
        {
            if (hidePrefix && item.IsDirectory
                && TryExtractLeadingPrefix(item.FileName, out _, out var prefixLength))
            {
                var withoutPrefix = item.FileName[prefixLength..].TrimStart();
                item.DisplayName = withoutPrefix.Length == 0 ? item.FileName : withoutPrefix;
            }
            else
            {
                item.DisplayName = item.FileName;
            }
        }
    }

    private void ApplyPrefixOptionSearch()
    {
        if (PrefixSearchBox is null) return;
        var query = PrefixSearchBox.Text.Trim();
        _visiblePrefixOptions.ReplaceAll(string.IsNullOrEmpty(query)
            ? _allPrefixOptions
            : _allPrefixOptions.Where(option =>
                option.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void UpdatePrefixFilterSummary()
    {
        if (PrefixFilterToggle is null) return;
        PrefixFilterToggle.Content = _selectedPrefixKeys.Count switch
        {
            0 => "접두어: 전체",
            1 => $"접두어: {_allPrefixOptions.FirstOrDefault(option => option.IsSelected)?.Name ?? "1개"}",
            _ => $"접두어: {_selectedPrefixKeys.Count:N0}개"
        };
        PrefixSelectionText.Text = _selectedPrefixKeys.Count == 0
            ? $"{_allPrefixOptions.Count:N0}개"
            : $"선택 {_selectedPrefixKeys.Count:N0}개";
    }

    private void PrefixFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        PrefixFilterPopup.IsOpen = PrefixFilterToggle.IsChecked == true;
        if (!PrefixFilterPopup.IsOpen) return;
        Dispatcher.BeginInvoke(() =>
        {
            PrefixSearchBox.Focus();
            PrefixSearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void PrefixFilterPopup_Closed(object sender, EventArgs e) =>
        PrefixFilterToggle.IsChecked = false;

    private void PrefixSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyPrefixOptionSearch();

    private void PrefixOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: PrefixFilterOption option }) return;
        if (option.IsSelected) _selectedPrefixKeys.Add(option.Key);
        else _selectedPrefixKeys.Remove(option.Key);
        ApplyPrefixFilter();
    }

    private void ClearPrefixFilter_Click(object sender, RoutedEventArgs e)
    {
        _selectedPrefixKeys.Clear();
        foreach (var option in _allPrefixOptions) option.IsSelected = false;
        ApplyPrefixFilter();
    }

    private void HidePrefix_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _settings.HidePrefix = HidePrefixCheckBox.IsChecked == true;
        ApplyPrefixDisplay(_prefixFilterSource);
    }

    private void ShowExplorerPage(bool scrollToBottom = false, double? verticalOffset = null)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_images.Count / (double)ExplorerPageSize));
        _currentExplorerPage = Math.Clamp(_currentExplorerPage, 0, pageCount - 1);
        var firstIndex = _currentExplorerPage * ExplorerPageSize;
        var pageItems = _images.Skip(firstIndex).Take(ExplorerPageSize).ToList();
        var pageItemSet = pageItems.ToHashSet();

        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = new CancellationTokenSource();
        var token = _thumbnailCancellation.Token;

        foreach (var item in _images)
        {
            if (pageItemSet.Contains(item)) continue;
            if (item.Thumbnail is not null) item.Thumbnail = null;
            item.ThumbnailLoadStarted = false;
        }

        _visibleImages.ReplaceAll(pageItems);
        var lastIndex = firstIndex + pageItems.Count;
        PagePositionText.Text = _images.Count == 0
            ? "항목 없음"
            : $"{firstIndex + 1:N0}–{lastIndex:N0} / 전체 {_images.Count:N0}  ({_currentExplorerPage + 1:N0}/{pageCount:N0} 페이지)";
        PreviousPageButton.IsEnabled = _currentExplorerPage > 0;
        NextPageButton.IsEnabled = _currentExplorerPage + 1 < pageCount;

        Dispatcher.BeginInvoke(() =>
        {
            if (FindVisualChild<ScrollViewer>(ThumbnailList) is { } scrollViewer)
            {
                if (scrollToBottom) scrollViewer.ScrollToBottom();
                else if (verticalOffset is { } offset) scrollViewer.ScrollToVerticalOffset(offset);
                else scrollViewer.ScrollToTop();
            }
        }, DispatcherPriority.Loaded);

        _ = LoadThumbnailsAsync(pageItems, token);
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentExplorerPage <= 0) return;
        _currentExplorerPage--;
        ShowExplorerPage(scrollToBottom: true);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_images.Count / (double)ExplorerPageSize));
        if (_currentExplorerPage + 1 >= pageCount) return;
        _currentExplorerPage++;
        ShowExplorerPage();
    }

    private void PageJump_Click(object sender, RoutedEventArgs e) => JumpToExplorerPage();

    private void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        JumpToExplorerPage();
        e.Handled = true;
    }

    private void JumpToExplorerPage()
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_images.Count / (double)ExplorerPageSize));
        if (!int.TryParse(PageJumpBox.Text.Trim(), out var requestedPage)
            || requestedPage < 1
            || requestedPage > pageCount)
        {
            StatusText.Text = $"페이지 번호는 1부터 {pageCount:N0} 사이로 입력하세요.";
            PageJumpBox.Focus();
            PageJumpBox.SelectAll();
            return;
        }

        _currentExplorerPage = requestedPage - 1;
        ShowExplorerPage();
        PageJumpBox.Clear();
        ThumbnailList.Focus();
        StatusText.Text = $"{requestedPage:N0}페이지로 이동했습니다.";
    }

    private async Task ShowImageAsync(int index)
    {
        if (index < 0 || index >= _images.Count || !_images[index].IsImage) return;
        _currentIndex = index;
        ExplorerView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        await RefreshViewerImagesAsync();
        Focus();
    }

    private async Task RefreshViewerImagesAsync()
    {
        if (_currentIndex < 0 || _currentIndex >= _images.Count) return;

        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        _ocrCancellation = null;
        ResetActiveTextState();
        StopAnimation();
        _viewerCancellation?.Cancel();
        _viewerCancellation = new CancellationTokenSource();
        var token = _viewerCancellation.Token;
        var selectedPath = _images[_currentIndex].FullPath;

        try
        {
            var current = await _decoder.LoadAsync(selectedPath, null, token);
            if (token.IsCancellationRequested) return;
            if (current is null)
            {
                MessageBox.Show(this, "파일이 손상되었거나 지원하지 않는 이미지 인코딩입니다.",
                    "이미지 열기", MessageBoxButton.OK, MessageBoxImage.Information);
                ReturnToExplorer();
                return;
            }
            SetSingleImage(current);

            if (_viewerMode is ViewerMode.DualLeftToRight or ViewerMode.DualRightToLeft)
            {
                BitmapSource? next = null;
                var nextIndex = FindImageIndex(_currentIndex, 1, 1);
                if (nextIndex >= 0)
                    next = await _decoder.LoadAsync(_images[nextIndex].FullPath, null, token);
                if (token.IsCancellationRequested) return;

                if (_viewerMode == ViewerMode.DualLeftToRight)
                {
                    DualLeftImage.Source = current;
                    DualRightImage.Source = next;
                }
                else
                {
                    DualLeftImage.Source = next;
                    DualRightImage.Source = current;
                }
            }

            ViewerFileName.Text = _images[_currentIndex].FileName;
            var imageCount = _images.Count(item => item.IsImage);
            var imagePosition = _images.Take(_currentIndex + 1).Count(item => item.IsImage);
            ViewerPosition.Text =
                $"전체 이미지 {imageCount:N0}개 중 {imagePosition:N0}번째  ·  {current.PixelWidth:N0} × {current.PixelHeight:N0}";

            RestoreCachedTextState(
                selectedPath,
                showOverlay: !_continuousTranslationEnabled,
                restoreTargetLanguage: !_continuousTranslationEnabled);

            if (_continuousTranslationEnabled && IsContinuousTranslationFolder())
                _ = EnsureVisibleImagesTranslatedAsync(showErrors: false);

            if (_viewerMode is not (ViewerMode.DualLeftToRight or ViewerMode.DualRightToLeft)
                && _magickDecoder.CanDecodeAnimation(selectedPath))
            {
                var animation = await _magickDecoder.LoadAnimationAsync(selectedPath, token);
                if (!token.IsCancellationRequested && animation.IsAnimated)
                    StartAnimation(animation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Error("ImageViewer", $"이미지를 열지 못했습니다: {selectedPath}", ex);
            MessageBox.Show(this, $"이미지를 열 수 없습니다.\n\n{ex.Message}", "이미지 열기", MessageBoxButton.OK, MessageBoxImage.Warning);
            ReturnToExplorer();
        }
    }

    private void SetSingleImage(BitmapSource image)
    {
        OriginalImage.Source = image;
        FitImage.Source = image;
        FlexibleImage.Source = image;
    }

    private void StartAnimation(DecodedAnimation animation)
    {
        _animationFrames = animation.Frames;
        _animationFrameIndex = 0;
        ShowAnimationFrame();
    }

    private void StopAnimation()
    {
        _animationTimer.Stop();
        _animationFrames = [];
        _animationFrameIndex = 0;
    }

    private void ShowAnimationFrame()
    {
        if (_animationFrames.Count == 0) return;
        var frame = _animationFrames[_animationFrameIndex];
        SetSingleImage(frame.Image);
        _animationTimer.Interval = frame.Duration < TimeSpan.FromMilliseconds(20)
            ? TimeSpan.FromMilliseconds(20)
            : frame.Duration;
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_animationFrames.Count < 2)
        {
            StopAnimation();
            return;
        }

        _animationFrameIndex = (_animationFrameIndex + 1) % _animationFrames.Count;
        ShowAnimationFrame();
    }

    private void ApplyViewerMode()
    {
        OriginalScrollViewer.Visibility = Visibility.Collapsed;
        FitViewbox.Visibility = Visibility.Collapsed;
        FlexibleImage.Visibility = Visibility.Collapsed;
        DualImageGrid.Visibility = Visibility.Collapsed;

        switch (_viewerMode)
        {
            case ViewerMode.Original:
                OriginalScrollViewer.Visibility = Visibility.Visible;
                break;
            case ViewerMode.Fit:
                FitViewbox.Visibility = Visibility.Visible;
                break;
            case ViewerMode.Fill:
                FlexibleImage.Visibility = Visibility.Visible;
                FlexibleImage.Stretch = Stretch.UniformToFill;
                break;
            case ViewerMode.DualLeftToRight:
            case ViewerMode.DualRightToLeft:
                DualImageGrid.Visibility = Visibility.Visible;
                break;
            default:
                FlexibleImage.Visibility = Visibility.Visible;
                FlexibleImage.Stretch = Stretch.Uniform;
                break;
        }

        Dispatcher.BeginInvoke(RenderTranslationOverlay, DispatcherPriority.Loaded);
    }

    private void ReturnToExplorer()
    {
        _ocrCancellation?.Cancel();
        OcrPanel.Visibility = Visibility.Collapsed;
        _viewerCancellation?.Cancel();
        StopAnimation();
        ClearTranslationOverlay();
        ViewerView.Visibility = Visibility.Collapsed;
        ExplorerView.Visibility = Visibility.Visible;
        if (_currentIndex >= 0)
        {
            _currentExplorerPage = _currentIndex / ExplorerPageSize;
            ShowExplorerPage();
            var currentItem = _images[_currentIndex];
            ThumbnailList.SelectedItem = currentItem;
            ThumbnailList.ScrollIntoView(currentItem);
            ThumbnailList.Focus();
        }
        if (_pendingFolderRefresh)
        {
            _pendingFolderRefresh = false;
            _ = RefreshFolderFromWatcherAsync();
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Text boxes handle Enter and editing keys themselves before explorer shortcuts.
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (ViewerView.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (ExitOnEscapeCheckBox.IsChecked == true) Close(); else ReturnToExplorer();
                    e.Handled = true;
                    return;
                case Key.Left:
                case Key.Up:
                case Key.PageUp:
                    await MoveAsync(-1);
                    e.Handled = true;
                    return;
                case Key.Right:
                case Key.Down:
                case Key.PageDown:
                case Key.Space:
                    await MoveAsync(1);
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    SelectMode(ViewerMode.Original);
                    break;
                case Key.D9:
                case Key.NumPad9:
                    SelectMode(ViewerMode.Fit);
                    break;
                case Key.D8:
                case Key.NumPad8:
                    SelectMode(ViewerMode.Fill);
                    break;
                case Key.D7:
                case Key.NumPad7:
                    SelectMode(ViewerMode.DualLeftToRight);
                    break;
                case Key.D6:
                case Key.NumPad6:
                    SelectMode(ViewerMode.DualRightToLeft);
                    break;
                case Key.Z:
                    SelectMode(ViewerMode.FitIncludingSmall);
                    break;
            }
        }
        else
        {
            var modifiers = Keyboard.Modifiers;
            if (e.Key == Key.Back || (e.Key == Key.Up && modifiers.HasFlag(ModifierKeys.Alt)))
            {
                await GoToParentFolderAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ThumbnailList.SelectedIndex >= 0)
            {
                await OpenSelectedItemAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                await LoadFolderAsync(_currentFolder);
                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                await RenameSelectedItemAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                await DeleteSelectedItemsAsync(
                    permanently: modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
            }
            else if (e.Key == Key.N && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                await CreateNewFolderAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.C && modifiers == ModifierKeys.Control)
            {
                CopySelectedItems(cut: false);
                e.Handled = true;
            }
            else if (e.Key == Key.X && modifiers == ModifierKeys.Control)
            {
                CopySelectedItems(cut: true);
                e.Handled = true;
            }
            else if (e.Key == Key.V && modifiers == ModifierKeys.Control)
            {
                await PasteItemsAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.A && modifiers == ModifierKeys.Control)
            {
                ThumbnailList.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (ExplorerView.Visibility != Visibility.Visible
            || FolderPathBox.IsKeyboardFocusWithin
            || Keyboard.FocusedElement is TextBoxBase
            || string.IsNullOrEmpty(e.Text)
            || e.Text.Any(char.IsControl))
            return;

        var continuingSearch = _typeSearchBuffer.Length > 0;
        _typeSearchBuffer += e.Text;
        _typeSearchResetTimer.Stop();
        _typeSearchResetTimer.Start();

        var currentItem = ThumbnailList.SelectedItem as ImageFileItem;
        var currentIndex = currentItem is null ? -1 : _images.IndexOf(currentItem);
        var matchIndex = FindTypeSearchMatch(
            _typeSearchBuffer,
            continuingSearch ? Math.Max(0, currentIndex) : currentIndex + 1);

        if (matchIndex < 0 && _typeSearchBuffer.Length > e.Text.Length)
        {
            _typeSearchBuffer = e.Text;
            matchIndex = FindTypeSearchMatch(_typeSearchBuffer, currentIndex + 1);
        }

        if (matchIndex >= 0)
        {
            var match = _images[matchIndex];
            var targetPage = matchIndex / ExplorerPageSize;
            if (_currentExplorerPage != targetPage)
            {
                _currentExplorerPage = targetPage;
                ShowExplorerPage();
            }

            Dispatcher.BeginInvoke(() =>
            {
                ThumbnailList.UnselectAll();
                ThumbnailList.SelectedItem = match;
                ThumbnailList.ScrollIntoView(match);
                ThumbnailList.Focus();
            }, DispatcherPriority.Loaded);
            StatusText.Text = $"빠른 찾기: {_typeSearchBuffer}";
        }
        else
        {
            StatusText.Text = $"'{_typeSearchBuffer}'(으)로 시작하는 항목이 없습니다.";
        }
        e.Handled = true;
    }

    private int FindTypeSearchMatch(string searchText, int startIndex)
    {
        if (_images.Count == 0 || string.IsNullOrEmpty(searchText)) return -1;
        startIndex = Math.Clamp(startIndex, 0, _images.Count - 1);
        for (var offset = 0; offset < _images.Count; offset++)
        {
            var index = (startIndex + offset) % _images.Count;
            if (_images[index].DisplayName.StartsWith(searchText, StringComparison.CurrentCultureIgnoreCase))
                return index;
        }
        return -1;
    }

    private void ResetTypeSearch()
    {
        _typeSearchResetTimer.Stop();
        _typeSearchBuffer = string.Empty;
    }

    private void SelectMode(ViewerMode mode)
    {
        ViewModeBox.SelectedIndex = (int)mode;
    }

    private async Task MoveAsync(int offset)
    {
        if (_isNavigating || _images.Count == 0) return;

        _isNavigating = true;
        try
        {
            var step = _viewerMode is ViewerMode.DualLeftToRight or ViewerMode.DualRightToLeft ? 2 : 1;
            var next = FindImageIndex(_currentIndex, Math.Sign(offset), step);
            if (next < 0 || next == _currentIndex) return;
            _currentIndex = next;
            await RefreshViewerImagesAsync();
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private int FindImageIndex(int startIndex, int direction, int imageSteps)
    {
        if (direction == 0 || imageSteps < 1) return -1;
        var remaining = imageSteps;
        for (var index = startIndex + direction; index >= 0 && index < _images.Count; index += direction)
        {
            if (!_images[index].IsImage) continue;
            remaining--;
            if (remaining == 0) return index;
        }
        return -1;
    }

    private async Task OpenSelectedItemAsync()
    {
        if (ThumbnailList.SelectedItem is not ImageFileItem item) return;
        if (item.IsDirectory)
        {
            await LoadFolderAsync(item.FullPath);
            return;
        }

        if (item.IsImage)
        {
            var fullIndex = _images.IndexOf(item);
            if (fullIndex >= 0) await ShowImageAsync(fullIndex);
        }
    }

    private IReadOnlyList<ImageFileItem> GetSelectedExistingItems() => ThumbnailList.SelectedItems
        .OfType<ImageFileItem>()
        .Where(item => item.IsDirectory ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath))
        .ToList();

    private async void OpenItemFromContextMenu_Click(object sender, RoutedEventArgs e) =>
        await OpenSelectedItemAsync();

    private void CopyItems_Click(object sender, RoutedEventArgs e) => CopySelectedItems(cut: false);

    private void CutItems_Click(object sender, RoutedEventArgs e) => CopySelectedItems(cut: true);

    private async void PasteItems_Click(object sender, RoutedEventArgs e) => await PasteItemsAsync();

    private async void RenameItem_Click(object sender, RoutedEventArgs e) => await RenameSelectedItemAsync();

    private async void DeleteItems_Click(object sender, RoutedEventArgs e) => await DeleteSelectedItemsAsync();

    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await CreateNewFolderAsync();

    private void CopySelectedItems(bool cut)
    {
        var items = GetSelectedExistingItems();
        if (items.Count == 0) return;

        try
        {
            var paths = new StringCollection();
            paths.AddRange(items.Select(item => item.FullPath).ToArray());
            Clipboard.SetFileDropList(paths);
            _cutClipboardPaths.Clear();
            if (cut)
                foreach (var item in items)
                    _cutClipboardPaths.Add(item.FullPath);
            StatusText.Text = cut
                ? $"{items.Count:N0}개 항목을 잘라냈습니다. 이동할 폴더에서 붙여넣으세요."
                : $"{items.Count:N0}개 항목을 복사했습니다.";
        }
        catch (Exception ex)
        {
            AppLogService.Warning("FileClipboard", "선택 항목을 클립보드에 넣지 못했습니다.", ex);
            MessageBox.Show(this, "선택 항목을 클립보드에 넣지 못했습니다.", "파일 작업",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PasteItemsAsync()
    {
        StringCollection clipboardPaths;
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                StatusText.Text = "붙여넣을 파일이나 폴더가 클립보드에 없습니다.";
                return;
            }
            clipboardPaths = Clipboard.GetFileDropList();
        }
        catch (Exception ex)
        {
            AppLogService.Warning("FileClipboard", "클립보드를 읽지 못했습니다.", ex);
            return;
        }

        var sources = clipboardPaths.Cast<string>()
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sources.Count == 0) return;

        var isMove = sources.All(path => _cutClipboardPaths.Contains(path));
        var completed = 0;
        foreach (var source in sources)
        {
            var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (string.IsNullOrWhiteSpace(sourceName)) continue;
            var destination = Path.Combine(_currentFolder, sourceName);

            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                if (isMove) continue;
                destination = GetAvailableCopyPath(destination, Directory.Exists(source));
            }

            if (Directory.Exists(source) && IsSameOrChildPath(destination, source))
            {
                MessageBox.Show(this, $"폴더를 자기 자신 안으로 복사하거나 이동할 수 없습니다.\n\n{source}",
                    "파일 작업", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }

            var destinationExists = File.Exists(destination) || Directory.Exists(destination);
            if (destinationExists)
            {
                var answer = MessageBox.Show(this,
                    $"대상 위치에 같은 이름의 항목이 있습니다. 덮어쓰시겠습니까?\n\n{destination}",
                    isMove ? "이동" : "복사", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (answer == MessageBoxResult.Cancel) break;
                if (answer != MessageBoxResult.Yes) continue;
            }

            try
            {
                StatusText.Text = $"{sourceName} {(isMove ? "이동" : "복사")} 중…";
                await Task.Run(() => CopyOrMovePath(source, destination, isMove));
                if (isMove)
                    await _tagStore.RemapPathsAsync(source, destination);
                else
                    await _tagStore.CopyPathsAsync(source, destination);
                completed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLogService.Error("FileOperation", $"파일 작업에 실패했습니다: {source} -> {destination}", ex);
                MessageBox.Show(this, $"'{sourceName}'을(를) 처리하지 못했습니다.\n\n{ex.Message}",
                    "파일 작업", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (isMove && completed > 0)
        {
            _cutClipboardPaths.Clear();
            try { Clipboard.Clear(); } catch { }
        }
        await RefreshFolderFromWatcherAsync();
        await RefreshTagCloudAsync();
        StatusText.Text = $"{completed:N0}개 항목을 {(isMove ? "이동" : "복사")}했습니다.";
    }

    private static void CopyOrMovePath(string source, string destination, bool move)
    {
        if (Directory.Exists(source))
        {
            if (move) FileSystem.MoveDirectory(source, destination, overwrite: true);
            else FileSystem.CopyDirectory(source, destination, overwrite: true);
        }
        else
        {
            if (move) FileSystem.MoveFile(source, destination, overwrite: true);
            else File.Copy(source, destination, overwrite: true);
        }
    }

    private static string GetAvailableCopyPath(string requestedPath, bool isDirectory)
    {
        var parent = Path.GetDirectoryName(requestedPath) ?? string.Empty;
        var extension = isDirectory ? string.Empty : Path.GetExtension(requestedPath);
        var name = isDirectory ? Path.GetFileName(requestedPath) : Path.GetFileNameWithoutExtension(requestedPath);
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " - 복사본" : $" - 복사본 ({number})";
            var candidate = Path.Combine(parent, name + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        var candidateFull = Path.GetFullPath(candidate);
        var parentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(candidateFull, parentFull, StringComparison.OrdinalIgnoreCase)
            || candidateFull.StartsWith(parentFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RenameSelectedItemAsync()
    {
        var items = GetSelectedExistingItems();
        if (items.Count != 1)
        {
            StatusText.Text = "이름을 바꿀 항목 하나를 선택하세요.";
            return;
        }

        var item = items[0];
        var dialog = new TextInputWindow("이름 바꾸기", "새 이름을 입력하세요.", item.FileName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var newName = dialog.Value;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "파일이나 폴더 이름에 사용할 수 없는 문자가 포함되어 있습니다.", "이름 바꾸기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var destination = Path.Combine(Path.GetDirectoryName(item.FullPath)!, newName);
        if (string.Equals(item.FullPath, destination, StringComparison.Ordinal)) return;
        if (!string.Equals(item.FullPath, destination, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(destination) || Directory.Exists(destination)))
        {
            MessageBox.Show(this, "같은 이름의 파일이나 폴더가 이미 있습니다.", "이름 바꾸기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (item.IsDirectory) Directory.Move(item.FullPath, destination);
            else File.Move(item.FullPath, destination);
            await _tagStore.RemapPathsAsync(item.FullPath, destination);
            await RefreshFolderFromWatcherAsync();
            await RefreshTagCloudAsync();
            StatusText.Text = $"'{newName}'(으)로 이름을 바꿨습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"이름을 바꾸지 못했습니다.\n\n{ex.Message}", "이름 바꾸기",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DeleteSelectedItemsAsync(bool permanently = false)
    {
        var items = GetSelectedExistingItems();
        if (items.Count == 0) return;
        var actionText = permanently ? "즉시 영구 삭제" : "휴지통으로 이동";
        var warningText = permanently
            ? $"선택한 {items.Count:N0}개 항목을 즉시 삭제하시겠습니까?\n\n이 작업은 휴지통을 거치지 않으며 되돌릴 수 없습니다."
            : $"선택한 {items.Count:N0}개 항목을 휴지통으로 이동하시겠습니까?";
        var answer = MessageBox.Show(this,
            warningText, actionText, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var deleted = 0;
        foreach (var item in items)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (item.IsDirectory)
                        FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs,
                            permanently ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin);
                    else
                        FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs,
                            permanently ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin);
                });
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                AppLogService.Warning("FileDelete", $"항목을 휴지통으로 이동하지 못했습니다: {item.FullPath}", ex);
            }
        }
        await RefreshFolderFromWatcherAsync();
        StatusText.Text = permanently
            ? $"{deleted:N0}개 항목을 영구 삭제했습니다."
            : $"{deleted:N0}개 항목을 휴지통으로 이동했습니다.";
    }

    private async Task CreateNewFolderAsync()
    {
        var initialName = "새 폴더";
        var dialog = new TextInputWindow("새 폴더", "새 폴더 이름을 입력하세요.", initialName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "폴더 이름에 사용할 수 없는 문자가 포함되어 있습니다.", "새 폴더",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = Path.Combine(_currentFolder, dialog.Value);
        if (Directory.Exists(path) || File.Exists(path))
        {
            MessageBox.Show(this, "같은 이름의 항목이 이미 있습니다.", "새 폴더",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Directory.CreateDirectory(path);
            await RefreshFolderFromWatcherAsync();
            StatusText.Text = $"폴더 '{dialog.Value}'을(를) 만들었습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"폴더를 만들지 못했습니다.\n\n{ex.Message}", "새 폴더",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void ThumbnailList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = source is null
            ? null
            : ItemsControl.ContainerFromElement(ThumbnailList, source) as ListBoxItem;
        if (container is not null) return;

        ThumbnailList.UnselectAll();
        ThumbnailList.Focus();
        ResetTypeSearch();
    }

    private void ThumbnailList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = ItemsControl.ContainerFromElement(ThumbnailList, source) as ListBoxItem;
        if (container is null)
        {
            e.Handled = true;
            return;
        }
        if (!container.IsSelected)
        {
            ThumbnailList.UnselectAll();
            container.IsSelected = true;
        }
        container.Focus();
    }

    private List<ImageFileItem> GetSelectedTagTargets() => ThumbnailList.SelectedItems
        .OfType<ImageFileItem>()
        .Where(item => item.IsDirectory ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath))
        .ToList();

    private async void AddTagFromContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedTagTargets();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "태그를 추가할 파일이나 폴더를 하나 이상 선택하세요.", "태그 추가",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TagCreationWindow(targets.Count) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Tags.Count == 0) return;
        await ApplyTagsToTargetsAsync(targets, dialog.Tags);
    }

    private async void ApplyTagFromContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedTagTargets();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "태그를 적용할 파일이나 폴더를 하나 이상 선택하세요.", "태그 적용",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var allTags = await _tagStore.GetAllTagsAsync();
        if (allTags.Count == 0)
        {
            MessageBox.Show(this, "아직 생성된 태그가 없습니다. 우클릭 메뉴의 '태그 추가'를 먼저 사용하세요.", "태그 적용",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TagAssignmentWindow(allTags, targets.Count, allowNewTags: false)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedTags.Count == 0) return;
        await ApplyTagsToTargetsAsync(targets, dialog.SelectedTags);
    }

    private async void RemoveTagFromContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedTagTargets();
        if (targets.Count == 0) return;

        var tagsByPath = await _tagStore.GetTagsForPathsAsync(targets.Select(item => item.FullPath));
        var assignedTags = tagsByPath.Values
            .SelectMany(tags => tags)
            .GroupBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new TagSummary(group.Key, group.Count()))
            .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (assignedTags.Count == 0)
        {
            MessageBox.Show(this, "선택한 작업물에는 지울 태그가 없습니다.", "태그 지우기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TagAssignmentWindow(
            assignedTags,
            targets.Count,
            allowNewTags: false,
            purpose: TagSelectionPurpose.Remove)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedTags.Count == 0) return;

        await _tagStore.RemoveTagsFromResourcesAsync(
            targets.Select(item => new TagResourceTarget(item.FullPath, item.IsDirectory)),
            dialog.SelectedTags);
        await RefreshItemTagsAsync(targets);
        StatusText.Text = $"작업물 {targets.Count:N0}개에서 선택한 태그를 지웠습니다.";
        await RefreshTagCloudAsync();
    }

    private async void RenameTagFromContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedTagTargets();
        if (targets.Count == 0) return;

        var tagsByPath = await _tagStore.GetTagsForPathsAsync(targets.Select(item => item.FullPath));
        var assignedTags = tagsByPath.Values
            .SelectMany(tags => tags)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (assignedTags.Count == 0)
        {
            MessageBox.Show(this, "선택한 작업물에는 수정할 태그가 없습니다.", "태그 수정하기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ShowRenameTagDialogAsync(assignedTags);
    }

    private async Task ShowRenameTagDialogAsync(IEnumerable<string> availableTags)
    {
        var dialog = new TagRenameWindow(availableTags) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (string.Equals(dialog.OriginalName, dialog.NewName, StringComparison.Ordinal)) return;

        await _tagStore.RenameTagAsync(dialog.OriginalName, dialog.NewName);
        if (_selectedTagNames.Remove(dialog.OriginalName))
            _selectedTagNames.Add(dialog.NewName);
        await RefreshItemTagsAsync(_images);
        await RefreshTagCloudAsync();
        StatusText.Text = $"태그 '{dialog.OriginalName}'의 이름을 '{dialog.NewName}'(으)로 변경했습니다.";
    }

    private async Task ApplyTagsToTargetsAsync(
        IReadOnlyList<ImageFileItem> targets,
        IReadOnlyList<string> tagsToApply)
    {
        await _tagStore.AddTagsToResourcesAsync(
            targets.Select(item => new TagResourceTarget(item.FullPath, item.IsDirectory)),
            tagsToApply);

        await RefreshItemTagsAsync(targets);

        StatusText.Text = $"작업물 {targets.Count:N0}개에 태그 {tagsToApply.Count:N0}개를 적용했습니다.";
        await RefreshTagCloudAsync();
    }

    private async Task RefreshItemTagsAsync(IEnumerable<ImageFileItem> items)
    {
        var itemList = items.ToList();
        var updatedTags = await _tagStore.GetTagsForPathsAsync(itemList.Select(item => item.FullPath));
        foreach (var item in itemList)
            item.TagsText = updatedTags.TryGetValue(item.FullPath, out var tags)
                ? string.Join(", ", tags)
                : string.Empty;
    }

    private async void SearchTags_Click(object sender, RoutedEventArgs e)
    {
        _tagFilterDebounceCancellation?.Cancel();
        var generation = ++_tagFilterGeneration;
        await ApplySelectedTagFilterAsync(generation, showEmptySelectionMessage: true);
    }

    private async Task ApplySelectedTagFilterAsync(int generation, bool showEmptySelectionMessage = false)
    {
        var tags = _selectedTagNames
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (tags.Count == 0)
        {
            if (showEmptySelectionMessage)
                MessageBox.Show(this, "위의 전체 태그 목록에서 모아볼 태그를 하나 이상 선택하세요.", "태그 검색",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ClearTagSearchButton.Visibility != Visibility.Visible)
        {
            _tagFilterReturnState = CaptureExplorerLocation();
            _tagFilterReturnFolder = NormalizeFolderLocationKey(_currentFolder);
            _folderLocations[_tagFilterReturnFolder] = _tagFilterReturnState;
        }

        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = new CancellationTokenSource();
        var token = _thumbnailCancellation.Token;
        var mode = TagMatchModeBox.SelectedIndex == 1 ? TagMatchMode.Or : TagMatchMode.And;
        var matches = await _tagStore.SearchAsync(_currentFolder, tags, mode);
        if (generation != _tagFilterGeneration || token.IsCancellationRequested) return;
        var items = matches
            .Where(match => match.IsDirectory ? Directory.Exists(match.Path) : File.Exists(match.Path))
            .Select(match =>
            {
                var item = new ImageFileItem(match.Path, match.IsDirectory, !match.IsDirectory && _decoder.CanDecode(match.Path));
                item.TagsText = match.TagsText;
                return item;
            })
            .ToList();
        items = SortExplorerItems(items);

        SetExplorerItems(items);
        ClearTagSearchButton.Visibility = Visibility.Visible;
        StatusText.Text = $"태그 {mode.ToString().ToUpperInvariant()} 검색 결과 {items.Count:N0}개";
    }

    private async Task RefreshTagSetsAsync(long? preferredTagSetId = null)
    {
        var sets = await _tagStore.GetTagSetsAsync();
        _isRefreshingTagSets = true;
        try
        {
            _tagSets.ReplaceAll(sets);
            var selected = sets.FirstOrDefault(set => set.Id == preferredTagSetId)
                           ?? sets.FirstOrDefault();
            if (selected is null) return;

            _tagStore.ActiveTagSetId = selected.Id;
            _settings.ActiveTagSetId = selected.Id;
            TagSetBox.SelectedValue = selected.Id;
        }
        finally
        {
            _isRefreshingTagSets = false;
        }
    }

    private async void TagSetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingTagSets || TagSetBox.SelectedItem is not TagSetSummary selected) return;
        if (_tagStore.ActiveTagSetId == selected.Id) return;

        _tagFilterDebounceCancellation?.Cancel();
        _tagFilterGeneration++;
        _selectedTagNames.Clear();
        _tagStore.ActiveTagSetId = selected.Id;
        _settings.ActiveTagSetId = selected.Id;
        await _settingsStore.SaveAsync(_settings);

        if (ClearTagSearchButton.Visibility == Visibility.Visible)
            await RestoreBeforeTagFilterAsync();
        else
            await RefreshItemTagsAsync(_prefixFilterSource);
        await RefreshTagCloudAsync();
        StatusText.Text = $"태그 세트를 '{selected.Name}'(으)로 전환했습니다.";
    }

    private async void AddTagSet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputWindow("새 태그 세트", "새 태그 세트 이름을 입력하세요.") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var id = await _tagStore.CreateTagSetAsync(dialog.Value);
            var restoreFilteredView = ClearTagSearchButton.Visibility == Visibility.Visible;
            _selectedTagNames.Clear();
            await RefreshTagSetsAsync(id);
            if (restoreFilteredView)
                await RestoreBeforeTagFilterAsync();
            else
                await RefreshItemTagsAsync(_prefixFilterSource);
            await RefreshTagCloudAsync();
            _settings.ActiveTagSetId = id;
            await _settingsStore.SaveAsync(_settings);
            StatusText.Text = $"태그 세트 '{dialog.Value}'을(를) 만들었습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"태그 세트를 만들 수 없습니다.\n\n{ex.Message}", "태그 세트",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RenameTagSet_Click(object sender, RoutedEventArgs e)
    {
        if (TagSetBox.SelectedItem is not TagSetSummary selected) return;
        var dialog = new TextInputWindow("태그 세트 이름 변경", "새 이름을 입력하세요.", selected.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _tagStore.RenameTagSetAsync(selected.Id, dialog.Value);
            await RefreshTagSetsAsync(selected.Id);
            StatusText.Text = $"태그 세트 이름을 '{dialog.Value}'(으)로 변경했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"태그 세트 이름을 변경할 수 없습니다.\n\n{ex.Message}", "태그 세트",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteTagSet_Click(object sender, RoutedEventArgs e)
    {
        if (TagSetBox.SelectedItem is not TagSetSummary selected) return;
        var answer = MessageBox.Show(this,
            $"태그 세트 '{selected.Name}'을(를) 삭제할까요?\n\n세트 안의 태그와 모든 적용 정보도 함께 삭제됩니다. 삭제 전 안전 백업을 만듭니다.",
            "태그 세트 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _tagStore.CreateSafetyBackupAsync("before-tagset-delete");
            await _tagStore.DeleteTagSetAsync(selected.Id);
            _tagFilterDebounceCancellation?.Cancel();
            _tagFilterGeneration++;
            _selectedTagNames.Clear();
            ClearTagSearchButton.Visibility = Visibility.Collapsed;
            _tagFilterReturnState = null;
            _tagFilterReturnFolder = null;
            await RefreshTagSetsAsync();
            await RefreshTagCloudAsync();
            await LoadFolderAsync(_currentFolder, recordHistory: false);
            await _settingsStore.SaveAsync(_settings);
            StatusText.Text = $"태그 세트 '{selected.Name}'을(를) 삭제했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "태그 세트 삭제",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshTagCloudAsync()
    {
        var tags = await _tagStore.GetAllTagsAsync();
        var previouslySelected = new HashSet<string>(_selectedTagNames, StringComparer.CurrentCultureIgnoreCase);
        _selectedTagNames.Clear();
        TagCloudPanel.Children.Clear();

        foreach (var tag in tags)
        {
            var color = TagChipColors[GetStableTagColorIndex(tag.Name)];
            var button = new ToggleButton
            {
                Content = tag.Name,
                Tag = tag.Name,
                IsChecked = previouslySelected.Contains(tag.Name),
                ToolTip = $"{tag.UsageCount:N0}개 항목에 사용 중",
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(3, 2, 3, 2),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(color)
            };
            button.Click += TagChip_Click;
            var contextMenu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 36, 43)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 74))
            };
            var renameItem = new MenuItem { Header = "태그 수정하기", Tag = tag.Name };
            renameItem.Click += RenameTagFromCloud_Click;
            var deleteItem = new MenuItem { Header = "태그 지우기", Tag = tag };
            deleteItem.Click += DeleteTagFromCloud_Click;
            contextMenu.Items.Add(renameItem);
            contextMenu.Items.Add(deleteItem);
            button.ContextMenu = contextMenu;
            if (button.IsChecked == true) _selectedTagNames.Add(tag.Name);
            UpdateTagChipAppearance(button, color);
            TagCloudPanel.Children.Add(button);
        }

        UpdateTagSelectionText();
    }

    private async void AddTagToLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TagCreationWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Tags.Count == 0) return;
        await _tagStore.CreateTagsAsync(dialog.Tags);
        await RefreshTagCloudAsync();
        StatusText.Text = $"태그 {dialog.Tags.Count:N0}개를 목록에 추가했습니다.";
    }

    private async void RenameTagFromCloud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tagName }) return;
        await ShowRenameTagDialogAsync([tagName]);
    }

    private async void DeleteTagFromCloud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TagSummary tag }) return;
        var usageText = tag.UsageCount == 0
            ? "아직 적용된 작업물은 없습니다."
            : $"이 태그는 현재 작업물 {tag.UsageCount:N0}개에 적용되어 있습니다.";
        var answer = MessageBox.Show(this,
            $"태그 '{tag.Name}'을(를) 완전히 지우시겠습니까?\n\n{usageText}\n모든 파일과 폴더에서도 이 태그가 제거됩니다.",
            "태그 지우기", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var wasSelectedFilter = _selectedTagNames.Contains(tag.Name);
        await _tagStore.DeleteTagAsync(tag.Name);
        _selectedTagNames.Remove(tag.Name);
        await RefreshItemTagsAsync(_images);
        await RefreshTagCloudAsync();
        StatusText.Text = $"태그 '{tag.Name}'을(를) 완전히 지웠습니다.";

        if (wasSelectedFilter)
        {
            if (_selectedTagNames.Count == 0) await RestoreBeforeTagFilterAsync();
            else await RefreshSelectedTagFilterAfterDelayAsync();
        }
    }

    private async void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tagName) return;
        if (button.IsChecked == true) _selectedTagNames.Add(tagName);
        else _selectedTagNames.Remove(tagName);
        UpdateTagChipAppearance(button, TagChipColors[GetStableTagColorIndex(tagName)]);
        UpdateTagSelectionText();
        await RefreshSelectedTagFilterAfterDelayAsync();
    }

    private async void ClearSelectedTags_Click(object sender, RoutedEventArgs e)
    {
        _tagFilterDebounceCancellation?.Cancel();
        _selectedTagNames.Clear();
        foreach (var button in TagCloudPanel.Children.OfType<ToggleButton>())
        {
            button.IsChecked = false;
            if (button.Tag is string tagName)
                UpdateTagChipAppearance(button, TagChipColors[GetStableTagColorIndex(tagName)]);
        }
        UpdateTagSelectionText();
        await RestoreBeforeTagFilterAsync();
    }

    private async void TagMatchModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedTagNames.Count > 0)
            await RefreshSelectedTagFilterAfterDelayAsync();
    }

    private async Task RefreshSelectedTagFilterAfterDelayAsync()
    {
        _tagFilterDebounceCancellation?.Cancel();
        _tagFilterDebounceCancellation = new CancellationTokenSource();
        var debounceToken = _tagFilterDebounceCancellation.Token;
        var generation = ++_tagFilterGeneration;

        try
        {
            await Task.Delay(180, debounceToken);
            if (debounceToken.IsCancellationRequested || generation != _tagFilterGeneration) return;

            if (_selectedTagNames.Count == 0)
            {
                await RestoreBeforeTagFilterAsync();
                return;
            }

            await ApplySelectedTagFilterAsync(generation);
        }
        catch (OperationCanceledException)
        {
            // A second chip click replaces this pending filter request.
        }
    }

    private void UpdateTagSelectionText() =>
        TagSelectionText.Text = $"선택 {_selectedTagNames.Count:N0}개";

    private static int GetStableTagColorIndex(string tagName)
    {
        var hash = 17;
        foreach (var character in tagName.ToUpperInvariant())
            hash = unchecked(hash * 31 + character);
        return (hash & int.MaxValue) % TagChipColors.Length;
    }

    private static void UpdateTagChipAppearance(ToggleButton button, Color color)
    {
        var selected = button.IsChecked == true;
        button.Background = new SolidColorBrush(selected
            ? Color.FromArgb(230, color.R, color.G, color.B)
            : Color.FromArgb(75, color.R, color.G, color.B));
        button.Foreground = Brushes.White;
        button.Opacity = selected ? 1 : 0.78;
    }

    private async void ClearTagSearch_Click(object sender, RoutedEventArgs e)
    {
        await RestoreBeforeTagFilterAsync();
    }

    private async Task RestoreBeforeTagFilterAsync()
    {
        ClearTagSearchButton.Visibility = Visibility.Collapsed;
        var currentFolderKey = NormalizeFolderLocationKey(_currentFolder);
        var restoreLocation = string.Equals(
            _tagFilterReturnFolder, currentFolderKey, StringComparison.OrdinalIgnoreCase)
            ? _tagFilterReturnState
            : null;
        if (restoreLocation is null)
            _folderLocations.TryGetValue(currentFolderKey, out restoreLocation);

        _tagFilterReturnState = null;
        _tagFilterReturnFolder = null;
        await LoadFolderAsync(
            _currentFolder,
            captureCurrentLocation: false,
            restoreLocation: restoreLocation);
    }

    private void ExplorerSort_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _prefixFilterSource.Count < 2) return;

        var selectedPath = (ThumbnailList.SelectedItem as ImageFileItem)?.FullPath;
        _prefixFilterSource = SortExplorerItems(_prefixFilterSource);
        _images.ReplaceAll(FilterItemsBySelectedPrefixes(_prefixFilterSource));

        if (selectedPath is not null)
        {
            var selectedItem = _images.FirstOrDefault(item =>
                string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedItem is not null)
            {
                _currentExplorerPage = _images.IndexOf(selectedItem) / ExplorerPageSize;
                ShowExplorerPage();
                ThumbnailList.SelectedItem = selectedItem;
                ThumbnailList.ScrollIntoView(selectedItem);
            }
            else ShowExplorerPage();
        }
        else ShowExplorerPage();

        StatusText.Text = $"{(SortFieldBox.SelectedItem as ComboBoxItem)?.Content} · "
            + (SortDirectionBox.SelectedIndex == 1 ? "내림차순" : "오름차순")
            + (FoldersFirstCheckBox.IsChecked == true ? " · 폴더 우선" : string.Empty);
    }

    private List<ImageFileItem> SortExplorerItems(IEnumerable<ImageFileItem> source)
    {
        var items = source.ToList();
        if (FoldersFirstCheckBox.IsChecked != true)
            return SortExplorerItemGroup(items).ToList();

        return SortExplorerItemGroup(items.Where(item => item.IsDirectory))
            .Concat(SortExplorerItemGroup(items.Where(item => !item.IsDirectory)))
            .ToList();
    }

    private IOrderedEnumerable<ImageFileItem> SortExplorerItemGroup(IEnumerable<ImageFileItem> items)
    {
        var descending = SortDirectionBox.SelectedIndex == 1;
        var field = Enum.IsDefined(typeof(ExplorerSortField), SortFieldBox.SelectedIndex)
            ? (ExplorerSortField)SortFieldBox.SelectedIndex
            : ExplorerSortField.Name;

        return field switch
        {
            ExplorerSortField.DateModified => descending
                ? items.OrderByDescending(item => item.DateModified).ThenByDescending(item => item.FileName, NaturalStringComparer.Instance)
                : items.OrderBy(item => item.DateModified).ThenBy(item => item.FileName, NaturalStringComparer.Instance),
            ExplorerSortField.DateCreated => descending
                ? items.OrderByDescending(item => item.DateCreated).ThenByDescending(item => item.FileName, NaturalStringComparer.Instance)
                : items.OrderBy(item => item.DateCreated).ThenBy(item => item.FileName, NaturalStringComparer.Instance),
            ExplorerSortField.Type => descending
                ? items.OrderByDescending(item => item.SortType, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(item => item.FileName, NaturalStringComparer.Instance)
                : items.OrderBy(item => item.SortType, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.FileName, NaturalStringComparer.Instance),
            ExplorerSortField.Size => descending
                ? items.OrderByDescending(item => item.SizeBytes ?? -1).ThenByDescending(item => item.FileName, NaturalStringComparer.Instance)
                : items.OrderBy(item => item.SizeBytes ?? -1).ThenBy(item => item.FileName, NaturalStringComparer.Instance),
            _ => descending
                ? items.OrderByDescending(item => item.FileName, NaturalStringComparer.Instance)
                : items.OrderBy(item => item.FileName, NaturalStringComparer.Instance)
        };
    }

    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static NaturalStringComparer Instance { get; } = new();

        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);
    }

    private async void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewerView.Visibility != Visibility.Visible || e.Delta == 0) return;

        e.Handled = true;
        await MoveAsync(e.Delta > 0 ? -1 : 1);
    }

    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var mouseButton = e.ChangedButton;
        if (mouseButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;

        e.Handled = true;
        if (ViewerView.Visibility == Visibility.Visible)
        {
            await MoveAsync(mouseButton == MouseButton.XButton1 ? -1 : 1);
            return;
        }

        if (_isMouseFolderNavigating)
        {
            StatusText.Text = "폴더를 불러오는 중입니다.";
            return;
        }

        _isMouseFolderNavigating = true;
        try
        {
            await NavigateFolderHistoryAsync(mouseButton == MouseButton.XButton1 ? -1 : 1);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "폴더 이동이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            AppLogService.Error("FolderNavigation", $"폴더 방문 기록 이동 실패: {_currentFolder}", ex);
            MessageBox.Show(this, $"폴더로 이동할 수 없습니다.\n\n{ex.Message}", "폴더 이동",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isMouseFolderNavigating = false;
        }
    }

    private void RecordFolderHistory(string folder)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (_folderHistoryIndex >= 0
            && string.Equals(_folderHistory[_folderHistoryIndex], normalized, StringComparison.OrdinalIgnoreCase))
            return;

        if (_folderHistoryIndex + 1 < _folderHistory.Count)
            _folderHistory.RemoveRange(_folderHistoryIndex + 1, _folderHistory.Count - _folderHistoryIndex - 1);

        _folderHistory.Add(normalized);
        _folderHistoryIndex = _folderHistory.Count - 1;
    }

    private async Task NavigateFolderHistoryAsync(int offset)
    {
        var targetIndex = _folderHistoryIndex + offset;
        if (targetIndex < 0)
        {
            StatusText.Text = "더 이전에 방문한 폴더가 없습니다.";
            return;
        }

        if (targetIndex >= _folderHistory.Count)
        {
            StatusText.Text = "앞으로 이동할 폴더 기록이 없습니다.";
            return;
        }

        var targetFolder = _folderHistory[targetIndex];
        if (!Directory.Exists(targetFolder))
        {
            StatusText.Text = "기록된 폴더가 삭제되었거나 더 이상 사용할 수 없습니다.";
            return;
        }

        var previousFolder = _currentFolder;
        await LoadFolderAsync(targetFolder, recordHistory: false);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(_currentFolder),
                Path.TrimEndingDirectorySeparator(targetFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            _folderHistoryIndex = targetIndex;
        }
        else
        {
            _currentFolder = previousFolder;
        }
    }

    private void ThumbnailSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        ApplyThumbnailSize(e.NewValue);
        _settings.ExplorerThumbnailSize = e.NewValue;
    }

    private void ApplyThumbnailSize(double requestedSize)
    {
        var size = Math.Clamp(requestedSize, 120, 320);
        Resources["ThumbnailItemWidth"] = size;
        Resources["ThumbnailPreviewHeight"] = Math.Max(96, size - 20);
    }

    private void ThumbnailList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var zoomNotches = Math.Max(1, Math.Abs(e.Delta) / 120);
            var change = zoomNotches * 10 * Math.Sign(e.Delta);
            ThumbnailSizeSlider.Value = Math.Clamp(ThumbnailSizeSlider.Value + change, 120, 320);
            e.Handled = true;
            return;
        }

        if (FindVisualChild<ScrollViewer>(ThumbnailList) is not { } scrollViewer)
            return;

        const double boundaryTolerance = 1.0;
        var pageCount = Math.Max(1, (int)Math.Ceiling(_images.Count / (double)ExplorerPageSize));
        var atTop = scrollViewer.VerticalOffset <= boundaryTolerance;
        var atBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - boundaryTolerance;

        if (e.Delta < 0 && atBottom && _currentExplorerPage + 1 < pageCount)
        {
            _currentExplorerPage++;
            ShowExplorerPage();
            e.Handled = true;
            return;
        }

        if (e.Delta > 0 && atTop && _currentExplorerPage > 0)
        {
            _currentExplorerPage--;
            ShowExplorerPage(scrollToBottom: true);
            e.Handled = true;
            return;
        }

        var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        var directionUp = e.Delta > 0;
        var configuredLines = SystemParameters.WheelScrollLines;

        if (configuredLines < 0)
        {
            for (var i = 0; i < notches * MouseWheelSpeedMultiplier; i++)
            {
                if (directionUp) scrollViewer.PageUp();
                else scrollViewer.PageDown();
            }
        }
        else
        {
            var lineCount = Math.Max(1, configuredLines) * notches * MouseWheelSpeedMultiplier;
            for (var i = 0; i < lineCount; i++)
            {
                if (directionUp) scrollViewer.LineUp();
                else scrollViewer.LineDown();
            }
        }

        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }

        return null;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _currentFolder, Title = "이미지 폴더 선택" };
        if (dialog.ShowDialog(this) == true)
            await LoadFolderAsync(dialog.FolderName);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        SaveUserSettings();
        var anchorPath = _visibleImages.FirstOrDefault()?.FullPath;
        _thumbnailCancellation?.Cancel();
        var dialog = new SettingsWindow(
            _settings, _thumbnailCacheStore, _tagStore, _builtInTranslatorService, _updateService) { Owner = this };
        var settingsAccepted = dialog.ShowDialog() == true;
        if (dialog.TagDataChanged)
        {
            await RefreshTagSetsAsync(_settings.ActiveTagSetId);
            await RefreshTagCloudAsync();
            await LoadFolderAsync(_currentFolder);
        }
        if (!settingsAccepted)
        {
            ShowExplorerPage();
            return;
        }

        _settings.ExplorerPageSize = dialog.ExplorerPageSize;
        _settings.MouseWheelSpeedMultiplier = dialog.MouseWheelSpeedMultiplier;
        _settings.ExplorerSortField = dialog.ExplorerSortField;
        _settings.ExplorerSortDescending = dialog.ExplorerSortDescending;
        _settings.ExplorerFoldersFirst = dialog.ExplorerFoldersFirst;
        _settings.ExitOnEscape = dialog.ExitOnEscape;
        _settings.TargetLanguageCode = dialog.TargetLanguageCode;
        _settings.TranslationProvider = dialog.TranslationProvider;
        _settings.QwenModelFolder = dialog.QwenModelFolder;
        await _builtInTranslatorService.ConfigureModelFolderAsync(_settings.QwenModelFolder);
        _settings.OllamaEndpoint = dialog.OllamaEndpoint;
        _settings.OllamaModel = dialog.OllamaModel;
        _settings.TranslationPrefetchEnabled = dialog.TranslationPrefetchEnabled;
        _settings.ThumbnailCacheMaxMegabytes = dialog.ThumbnailCacheMaxMegabytes;
        _settings.TagAutoBackupEnabled = dialog.TagAutoBackupEnabled;
        _settings.TagBackupRetentionCount = dialog.TagBackupRetentionCount;
        _settings.AutomaticUpdateCheckEnabled = dialog.AutomaticUpdateCheckEnabled;
        _settings.PrefixPatterns = dialog.PrefixPatterns.Select(pattern => pattern.Clone()).ToList();

        SortFieldBox.SelectedIndex = _settings.ExplorerSortField;
        SortDirectionBox.SelectedIndex = _settings.ExplorerSortDescending ? 1 : 0;
        FoldersFirstCheckBox.IsChecked = _settings.ExplorerFoldersFirst;
        ExitOnEscapeCheckBox.IsChecked = _settings.ExitOnEscape;
        TargetLanguageBox.SelectedValue = _settings.TargetLanguageCode;
        RebuildPrefixOptions();
        ApplyPrefixDisplay(_prefixFilterSource);
        _images.ReplaceAll(FilterItemsBySelectedPrefixes(_prefixFilterSource));

        if (anchorPath is not null)
        {
            var anchorIndex = _images.ToList().FindIndex(item =>
                string.Equals(item.FullPath, anchorPath, StringComparison.OrdinalIgnoreCase));
            _currentExplorerPage = anchorIndex < 0 ? 0 : anchorIndex / ExplorerPageSize;
        }
        else
        {
            _currentExplorerPage = 0;
        }
        ShowExplorerPage();
        await _thumbnailCacheStore.CleanupAsync(_settings.ThumbnailCacheMaxMegabytes);
        SaveUserSettings();
        StatusText.Text = "설정을 적용했습니다.";
    }

    private async void GoUp_Click(object sender, RoutedEventArgs e)
    {
        await GoToParentFolderAsync();
    }

    private async Task GoToParentFolderAsync()
    {
        DirectoryInfo? parent;
        try { parent = Directory.GetParent(_currentFolder); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning("FolderNavigation", $"상위 폴더를 확인하지 못했습니다: {_currentFolder}", ex);
            return;
        }

        if (parent is null)
        {
            StatusText.Text = "현재 위치보다 상위 폴더가 없습니다.";
            return;
        }
        await LoadFolderAsync(parent.FullName);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadFolderAsync(_currentFolder);

    private async void FolderPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await LoadFolderAsync(FolderPathBox.Text.Trim());
    }

    private async void ThumbnailList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ThumbnailList.SelectedIndex >= 0) await OpenSelectedItemAsync();
    }

    private void BackToExplorer_Click(object sender, RoutedEventArgs e) => ReturnToExplorer();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await MoveAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await MoveAsync(1);

    private async void ViewModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModeBox.SelectedIndex < 0) return;
        _viewerMode = (ViewerMode)ViewModeBox.SelectedIndex;
        ApplyViewerMode();
        if (ViewerView.Visibility == Visibility.Visible) await RefreshViewerImagesAsync();
    }

    private async Task RecognizeCurrentImageAsync(bool showPanel = true, bool showErrors = true)
    {
        if (_currentIndex < 0 || _currentIndex >= _images.Count || !_images[_currentIndex].IsImage) return;
        var imagePath = _images[_currentIndex].FullPath;
        if (showPanel) OcrPanel.Visibility = Visibility.Visible;
        if (RestoreCachedTextState(imagePath)) return;

        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        var ocrCancellation = new CancellationTokenSource();
        _ocrCancellation = ocrCancellation;
        var cancellationToken = ocrCancellation.Token;
        OcrPanelButton.IsEnabled = false;
        DetectedOcrLanguageText.Text = "언어 감지 중…";
        OcrSourceTextBox.Text = "문자를 인식하는 중…";
        TranslatedTextBox.Clear();

        try
        {
            OcrTextResult result;
            try
            {
                result = await RecognizeWithLocalEnginesAsync(imagePath, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(result.Text))
                    result = await _ocrService.RecognizeAsync(
                        imagePath, null, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogService.Warning("OCR", $"로컬 OCR 실패, Windows OCR로 대체: {imagePath}", ex);
                // Some older CPUs or unusual image encodings may not be accepted by
                // Paddle. Keep the existing Windows OCR as an automatic fallback.
                result = await _ocrService.RecognizeAsync(
                    imagePath, null, cancellationToken);
            }
            await StoreOcrResultAsync(imagePath, result);
            if (!IsCurrentImage(imagePath)) return;
            _lastOcrResult = result;
            _translatedOverlayLines = [];
            ClearTranslationOverlay();
            _lastDetectedOcrLanguage = result.RecognizedLanguageTag;
            OcrSourceTextBox.Text = string.IsNullOrWhiteSpace(result.Text) ? "인식된 문자가 없습니다." : result.Text;
            var detected = _ocrService.GetAvailableLanguages()
                .FirstOrDefault(language => language.LanguageTag.Equals(result.RecognizedLanguageTag, StringComparison.OrdinalIgnoreCase));
            var localLanguageName = result.RecognizedLanguageTag switch
            {
                "zh-Hans" => "중국어",
                "ja" => "일본어",
                "ko" => "한국어",
                "en" => "영어",
                _ => result.RecognizedLanguageTag
            };
            DetectedOcrLanguageText.Text = $"감지: {detected?.DisplayName ?? localLanguageName}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Error("OCR", $"문자 인식 실패: {imagePath}", ex);
            OcrSourceTextBox.Clear();
            if (showErrors)
                MessageBox.Show(this, $"문자 인식에 실패했습니다.\n\n{ex.Message}", "OCR",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else if (IsCurrentImage(imagePath))
                TranslationStatusText.Text = $"자동 문자 인식 실패: {ex.Message}";
        }
        finally
        {
            OcrPanelButton.IsEnabled = true;
        }
    }

    private async void TranslateText_Click(object sender, RoutedEventArgs e) =>
        await TranslateCurrentTextAsync(showErrors: true);

    private async Task<OcrTextResult> RecognizeWithLocalEnginesAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var paddle = await _localOcrService.RecognizeAsync(imagePath, cancellationToken);
        if (cancellationToken.IsCancellationRequested
            || !paddle.RecognizedLanguageTag.Equals("ja", StringComparison.OrdinalIgnoreCase))
            return paddle;

        try
        {
            var windows = await _ocrService.RecognizeAsync(imagePath, "ja", cancellationToken);
            return ScoreJapaneseOcr(windows) > ScoreJapaneseOcr(paddle) ? windows : paddle;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogService.Warning("OCR", $"Windows 일본어 OCR 보완 실패: {imagePath}", ex);
            return paddle;
        }
    }

    private static int ScoreJapaneseOcr(OcrTextResult result)
    {
        var score = 0;
        foreach (var rune in result.Text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30FF) score += 5;
            else if (value is >= 0x3400 and <= 0x9FFF) score += 2;
            else if (Rune.IsLetter(rune)) score -= 1;
            else if (Rune.IsDigit(rune)) score -= 2;
        }
        return score;
    }

    private async Task<bool> TranslateCurrentTextAsync(bool showErrors)
    {
        var text = OcrSourceTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "인식된 문자가 없습니다.") return false;
        if (_currentIndex < 0 || _currentIndex >= _images.Count) return false;
        var imagePath = _images[_currentIndex].FullPath;
        _ocrCancellation ??= new CancellationTokenSource();
        var cancellationToken = _ocrCancellation.Token;
        try
        {
            TranslatedTextBox.Text = "번역하는 중…";
            var targetItem = TargetLanguageBox.SelectedItem as ComboBoxItem;
            var targetName = targetItem?.Content?.ToString() ?? "한국어";
            var targetCode = targetItem?.Tag?.ToString() ?? "ko";
            var translation = _lastOcrResult is not null
                ? await TranslateOcrResultUsingConfiguredEngineAsync(
                    _lastOcrResult, targetName, targetCode, cancellationToken)
                : new TranslationOutput(
                    await TranslateUsingConfiguredEngineAsync(
                        text, _lastDetectedOcrLanguage, targetName, targetCode, cancellationToken),
                    []);
            var translatedText = translation.Text;
            var overlayLines = translation.OverlayLines;
            await StoreTranslationAsync(imagePath, translatedText, overlayLines, targetCode, overlayEnabled: true);
            if (!IsCurrentImage(imagePath)) return false;
            TranslatedTextBox.Text = translatedText;
            _translatedOverlayLines = overlayLines;
            _translationOverlayVisible = true;
            UpdateTranslationButton();
            OcrPanelButton.IsEnabled = true;
            RenderTranslationOverlay();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            AppLogService.Warning("Translation", $"번역 서버 연결 실패: {imagePath}", ex);
            if (!IsCurrentImage(imagePath)) return false;
            TranslatedTextBox.Clear();
            _translationOverlayVisible = false;
            UpdateTranslationButton();
            OcrPanelButton.IsEnabled = true;
            var isOllama = _settings.TranslationProvider == "Ollama";
            var message = isOllama
                ? "로컬 번역 서버에 연결할 수 없습니다. Ollama가 설치되어 실행 중인지 확인하세요.\n\n" +
                  "설치 후 터미널에서 ollama pull qwen3:1.7b 를 한 번 실행하면 됩니다."
                : "무료 온라인 번역 서비스에 연결할 수 없습니다. 인터넷 연결을 확인하거나 잠시 후 다시 시도하세요.";
            if (showErrors)
                MessageBox.Show(this, message, isOllama ? "로컬 번역" : "온라인 번역",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else
                TranslationStatusText.Text = message.Split('\n')[0];
            return false;
        }
        catch (Exception ex)
        {
            AppLogService.Error("Translation", $"번역 실패: {imagePath}", ex);
            if (!IsCurrentImage(imagePath)) return false;
            TranslatedTextBox.Clear();
            _translationOverlayVisible = false;
            UpdateTranslationButton();
            OcrPanelButton.IsEnabled = true;
            if (showErrors)
                MessageBox.Show(this, ex.Message, "번역", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                TranslationStatusText.Text = $"자동 번역 실패: {ex.Message}";
            return false;
        }
    }

    private Task<string> TranslateUsingConfiguredEngineAsync(
        string text,
        string sourceLanguage,
        string targetLanguageName,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        if (AreSameTranslationLanguage(sourceLanguage, targetLanguageCode))
            return Task.FromResult(text);

        return _settings.TranslationProvider switch
        {
            BuiltInQwenTranslatorService.ProviderId => _builtInTranslatorService.TranslateAsync(
                text, sourceLanguage, targetLanguageName, cancellationToken),
            "Ollama" => _translatorService.TranslateAsync(
                text, targetLanguageName, _settings.OllamaModel,
                _settings.OllamaEndpoint, cancellationToken),
            _ => _freeTranslatorService.TranslateAsync(
                text, sourceLanguage, targetLanguageCode, cancellationToken)
        };
    }

    private async Task<TranslationOutput> TranslateOcrResultUsingConfiguredEngineAsync(
        OcrTextResult ocrResult,
        string targetLanguageName,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        if (AreSameTranslationLanguage(ocrResult.RecognizedLanguageTag, targetLanguageCode))
        {
            var originals = ocrResult.Lines.Select(line => line.Text).ToList();
            return new TranslationOutput(
                string.Join(Environment.NewLine + Environment.NewLine, originals), originals);
        }

        if (_settings.TranslationProvider == BuiltInQwenTranslatorService.ProviderId
            && ocrResult.Lines.Count > 0)
        {
            var translatedBlocks = await _builtInTranslatorService.TranslateBlocksAsync(
                ocrResult.Lines.Select(line => line.Text).ToList(),
                ocrResult.RecognizedLanguageTag,
                targetLanguageName,
                cancellationToken);
            var blocks = translatedBlocks.Select(block => block.Trim()).ToList();
            return new TranslationOutput(
                string.Join(Environment.NewLine + Environment.NewLine, blocks), blocks);
        }

        var translatedText = await TranslateUsingConfiguredEngineAsync(
            ocrResult.Text, ocrResult.RecognizedLanguageTag,
            targetLanguageName, targetLanguageCode, cancellationToken);
        var overlayLines = System.Text.RegularExpressions.Regex
            .Split(translatedText.Trim(), @"\r?\n\s*\r?\n")
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToList();
        return new TranslationOutput(translatedText, overlayLines);
    }

    private string CurrentTranslationCacheProvider =>
        _settings.TranslationProvider == BuiltInQwenTranslatorService.ProviderId
            ? BuiltInQwenTranslatorService.CacheProviderId
            : _settings.TranslationProvider;

    private static bool AreSameTranslationLanguage(string source, string target)
    {
        if (source.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        var sourceBase = source.Split('-', '_')[0];
        var targetBase = target.Split('-', '_')[0];
        return string.Equals(sourceBase, targetBase, StringComparison.OrdinalIgnoreCase);
    }

    private async void ShowOcrPanel_Click(object sender, RoutedEventArgs e) => await RecognizeCurrentImageAsync();
    private async void TranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _images.Count || !_images[_currentIndex].IsImage) return;

        if (_continuousTranslationEnabled)
        {
            var imagePath = _images[_currentIndex].FullPath;
            var companionIndex = _viewerMode is ViewerMode.DualLeftToRight or ViewerMode.DualRightToLeft
                ? FindImageIndex(_currentIndex, 1, 1)
                : -1;
            DisableContinuousTranslation();
            await SetCachedOverlayEnabledAsync(imagePath, false);
            if (companionIndex >= 0)
                await SetCachedOverlayEnabledAsync(_images[companionIndex].FullPath, false);
            return;
        }

        _continuousTranslationEnabled = true;
        _continuousTranslationFolder = NormalizeFolderLocationKey(_currentFolder);
        UpdateTranslationButton();
        await EnsureVisibleImagesTranslatedAsync(showErrors: true);
    }

    private async Task EnsureVisibleImagesTranslatedAsync(bool showErrors)
    {
        if (!_continuousTranslationEnabled || !IsContinuousTranslationFolder()
            || _currentIndex < 0 || _currentIndex >= _images.Count)
            return;

        var anchorPath = _images[_currentIndex].FullPath;
        var currentTask = EnsureCurrentImageTranslatedAsync(showErrors);
        var targetItem = TargetLanguageBox.SelectedItem as ComboBoxItem;
        var targetName = targetItem?.Content?.ToString() ?? "한국어";
        var targetCode = targetItem?.Tag?.ToString() ?? "ko";
        if (_viewerMode is not (ViewerMode.DualLeftToRight or ViewerMode.DualRightToLeft))
        {
            await currentTask;
            if (IsCurrentImage(anchorPath) && _continuousTranslationEnabled && IsContinuousTranslationFolder())
            {
                UpdateTranslationButton();
                StartTranslationPrefetch(anchorPath, _currentIndex, targetName, targetCode);
            }
            return;
        }

        var companionIndex = FindImageIndex(_currentIndex, 1, 1);
        if (companionIndex < 0)
        {
            await currentTask;
            if (IsCurrentImage(anchorPath) && _continuousTranslationEnabled && IsContinuousTranslationFolder())
                StartTranslationPrefetch(anchorPath, _currentIndex, targetName, targetCode);
            return;
        }

        var companionPath = _images[companionIndex].FullPath;
        _ocrCancellation ??= new CancellationTokenSource();
        var cancellationToken = _ocrCancellation.Token;
        var companionTask = EnsureImageTranslationCachedAsync(
            companionPath, targetName, targetCode, cancellationToken, showErrors: false);
        TranslationStatusText.Text = "연속 번역: 두 페이지를 동시에 처리하고 있습니다.";

        await Task.WhenAll(currentTask, companionTask);

        if (IsCurrentImage(anchorPath) && _continuousTranslationEnabled && IsContinuousTranslationFolder())
        {
            UpdateTranslationButton();
            RenderTranslationOverlay();
            StartTranslationPrefetch(anchorPath, companionIndex, targetName, targetCode);
        }
    }

    private void StartTranslationPrefetch(
        string anchorPath,
        int lastVisibleIndex,
        string targetName,
        string targetCode)
    {
        if (!_settings.TranslationPrefetchEnabled) return;
        _ocrCancellation ??= new CancellationTokenSource();
        _ = PrefetchUpcomingTranslationsAsync(
            anchorPath, lastVisibleIndex, targetName, targetCode, _ocrCancellation.Token);
    }

    private async Task PrefetchUpcomingTranslationsAsync(
        string anchorPath,
        int lastVisibleIndex,
        string targetName,
        string targetCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextIndex = lastVisibleIndex;
            for (var count = 0; count < TranslationPrefetchImageCount; count++)
            {
                if (!_settings.TranslationPrefetchEnabled || !_continuousTranslationEnabled
                    || !IsContinuousTranslationFolder() || !IsCurrentImage(anchorPath))
                    return;

                nextIndex = FindImageIndex(nextIndex, 1, 1);
                if (nextIndex < 0) return;
                await EnsureImageTranslationCachedAsync(
                    _images[nextIndex].FullPath, targetName, targetCode, cancellationToken, showErrors: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Warning("Translation", $"다음 페이지 번역 미리 준비 실패: {anchorPath}", ex);
        }
    }

    private async Task<bool> EnsureImageTranslationCachedAsync(
        string imagePath,
        string targetName,
        string targetCode,
        CancellationToken cancellationToken,
        bool showErrors)
    {
        try
        {
            if (_imageTextCache.TryGetValue(imagePath, out var cached) && cached.IsCurrent(imagePath) &&
                !string.IsNullOrWhiteSpace(cached.TranslatedText) && cached.OverlayLines.Count > 0 &&
                string.Equals(cached.TargetLanguageCode, targetCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cached.TranslationProvider, CurrentTranslationCacheProvider, StringComparison.OrdinalIgnoreCase))
            {
                if (!cached.OverlayEnabled)
                    await SetCachedOverlayEnabledAsync(imagePath, true);
                return true;
            }

            OcrTextResult ocrResult;
            if (cached is not null && cached.IsCurrent(imagePath) && !string.IsNullOrWhiteSpace(cached.OcrResult.Text))
            {
                ocrResult = cached.OcrResult;
            }
            else
            {
                try
                {
                    ocrResult = await RecognizeWithLocalEnginesAsync(imagePath, cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return false;
                    if (string.IsNullOrWhiteSpace(ocrResult.Text))
                        ocrResult = await _ocrService.RecognizeAsync(imagePath, null, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    AppLogService.Warning("OCR", $"두 페이지 보기 로컬 OCR 실패, Windows OCR로 대체: {imagePath}", ex);
                    ocrResult = await _ocrService.RecognizeAsync(imagePath, null, cancellationToken);
                }

                await StoreOcrResultAsync(imagePath, ocrResult);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(ocrResult.Text)) return false;

            var translation = await TranslateOcrResultUsingConfiguredEngineAsync(
                ocrResult, targetName, targetCode, cancellationToken);
            var translatedText = translation.Text;
            var overlayLines = translation.OverlayLines;
            await StoreTranslationAsync(imagePath, translatedText, overlayLines, targetCode, overlayEnabled: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogService.Error("Translation", $"두 페이지 보기 자동 번역 실패: {imagePath}", ex);
            if (showErrors)
                MessageBox.Show(this, ex.Message, "번역", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }

    private async Task EnsureCurrentImageTranslatedAsync(bool showErrors)
    {
        if (!_continuousTranslationEnabled || !IsContinuousTranslationFolder()
            || _currentIndex < 0 || _currentIndex >= _images.Count || !_images[_currentIndex].IsImage)
            return;

        var imagePath = _images[_currentIndex].FullPath;
        var requestedTargetCode = (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ko";
        if (_imageTextCache.TryGetValue(imagePath, out var cached) && cached.IsCurrent(imagePath) &&
            !string.IsNullOrWhiteSpace(cached.TranslatedText) && cached.OverlayLines.Count > 0 &&
            string.Equals(cached.TargetLanguageCode, requestedTargetCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(cached.TranslationProvider, CurrentTranslationCacheProvider, StringComparison.OrdinalIgnoreCase))
        {
            await SetCachedOverlayEnabledAsync(imagePath, true);
            if (IsCurrentImage(imagePath))
                RestoreCachedTextState(imagePath, showOverlay: true, restoreTargetLanguage: false);
            return;
        }

        OcrPanelButton.IsEnabled = false;
        OcrPanelButton.Content = _lastOcrResult is null ? "인식 중…" : "번역 중…";
        TranslationStatusText.Text = _lastOcrResult is null
            ? "연속 번역: 문자를 자동으로 인식하고 있습니다."
            : "연속 번역: 번역하고 있습니다.";
        if (_lastOcrResult is null)
            await RecognizeCurrentImageAsync(showPanel: false, showErrors: showErrors);
        if (!IsCurrentImage(imagePath) || _lastOcrResult is null)
        {
            OcrPanelButton.IsEnabled = true;
            UpdateTranslationButton();
            return;
        }

        if (string.IsNullOrWhiteSpace(OcrSourceTextBox.Text)
            || OcrSourceTextBox.Text == "인식된 문자가 없습니다.")
        {
            OcrPanelButton.IsEnabled = true;
            UpdateTranslationButton();
            TranslationStatusText.Text = "연속 번역: 인식된 문자가 없습니다. 다음 이미지로 이동하면 다시 시도합니다.";
            return;
        }

        TargetLanguageBox.SelectedValue = requestedTargetCode;
        OcrPanelButton.Content = "번역 중…";
        TranslationStatusText.Text = "연속 번역: 인식된 문장을 번역하고 있습니다.";
        var translated = await TranslateCurrentTextAsync(showErrors);
        if (IsCurrentImage(imagePath))
        {
            OcrPanelButton.IsEnabled = true;
            UpdateTranslationButton();
            if (!translated)
                TranslationStatusText.Text = "연속 번역을 완료하지 못했습니다. 다음 이미지에서 다시 시도합니다.";
        }
    }

    private async void TargetLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _restoringCachedTextState) return;
        if (_currentIndex >= 0 && _currentIndex < _images.Count)
            await SetCachedOverlayEnabledAsync(_images[_currentIndex].FullPath, false);
        ClearTranslationOverlay();
        _translatedOverlayLines = [];
        _translationOverlayVisible = false;
        UpdateTranslationButton();
        if (_continuousTranslationEnabled && ViewerView.Visibility == Visibility.Visible)
            await EnsureVisibleImagesTranslatedAsync(showErrors: true);
    }

    private void CloseOcrPanel_Click(object sender, RoutedEventArgs e) => OcrPanel.Visibility = Visibility.Collapsed;

    private void CopyOcrText_Click(object sender, RoutedEventArgs e)
    {
        var text = string.IsNullOrWhiteSpace(TranslatedTextBox.Text) ? OcrSourceTextBox.Text : TranslatedTextBox.Text;
        if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
    }

    private void ViewerImageArea_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTranslationOverlay();

    private void ClearTranslationOverlay() => TranslationOverlayCanvas.Children.Clear();

    private void ResetActiveTextState()
    {
        ClearTranslationOverlay();
        _lastOcrResult = null;
        _translatedOverlayLines = [];
        _lastDetectedOcrLanguage = "en";
        OcrSourceTextBox.Clear();
        TranslatedTextBox.Clear();
        DetectedOcrLanguageText.Text = "감지: -";
        _translationOverlayVisible = false;
        OcrPanelButton.IsEnabled = true;
        UpdateTranslationButton();
    }

    private bool RestoreCachedTextState(
        string imagePath,
        bool showOverlay = true,
        bool restoreTargetLanguage = true)
    {
        if (!_imageTextCache.TryGetValue(imagePath, out var entry) || !entry.IsCurrent(imagePath))
        {
            _imageTextCache.Remove(imagePath);
            _ = _ocrCacheStore.DeleteAsync(imagePath);
            return false;
        }

        _lastOcrResult = entry.OcrResult;
        _lastDetectedOcrLanguage = entry.OcrResult.RecognizedLanguageTag;
        var translationMatchesProvider = string.Equals(
            entry.TranslationProvider, CurrentTranslationCacheProvider, StringComparison.OrdinalIgnoreCase);
        _translatedOverlayLines = showOverlay && entry.OverlayEnabled && translationMatchesProvider
            ? entry.OverlayLines
            : [];
        OcrSourceTextBox.Text = entry.OcrResult.Text;
        TranslatedTextBox.Text = translationMatchesProvider ? entry.TranslatedText : string.Empty;
        if (translationMatchesProvider && restoreTargetLanguage && !string.IsNullOrWhiteSpace(entry.TargetLanguageCode))
        {
            _restoringCachedTextState = true;
            try { TargetLanguageBox.SelectedValue = entry.TargetLanguageCode; }
            finally { _restoringCachedTextState = false; }
        }
        SetDetectedLanguageLabel(entry.OcrResult.RecognizedLanguageTag);
        _translationOverlayVisible = showOverlay && entry.OverlayEnabled && translationMatchesProvider
            && entry.OverlayLines.Count > 0;
        UpdateTranslationButton();
        _ = TouchCacheSafelyAsync(imagePath);
        if (_translationOverlayVisible)
            Dispatcher.BeginInvoke(RenderTranslationOverlay, DispatcherPriority.Loaded);
        return true;
    }

    private async Task StoreOcrResultAsync(string imagePath, OcrTextResult result)
    {
        var info = new FileInfo(imagePath);
        var entry = new ImageTextCacheEntry(
            info.Length, info.LastWriteTimeUtc.Ticks, result, string.Empty, [], string.Empty, string.Empty, false);
        _imageTextCache[imagePath] = entry;
        await PersistCacheEntryAsync(imagePath, entry);
    }

    private async Task StoreTranslationAsync(
        string imagePath,
        string translatedText,
        IReadOnlyList<string> overlayLines,
        string targetLanguageCode,
        bool overlayEnabled)
    {
        if (_imageTextCache.TryGetValue(imagePath, out var entry) && entry.IsCurrent(imagePath))
        {
            var updated = entry with
            {
                TranslatedText = translatedText,
                OverlayLines = overlayLines,
                TargetLanguageCode = targetLanguageCode,
                TranslationProvider = CurrentTranslationCacheProvider,
                OverlayEnabled = overlayEnabled
            };
            _imageTextCache[imagePath] = updated;
            await PersistCacheEntryAsync(imagePath, updated);
        }
    }

    private async Task SetCachedOverlayEnabledAsync(string imagePath, bool enabled)
    {
        if (_imageTextCache.TryGetValue(imagePath, out var entry) && entry.IsCurrent(imagePath))
        {
            var updated = entry with { OverlayEnabled = enabled };
            _imageTextCache[imagePath] = updated;
            await PersistCacheEntryAsync(imagePath, updated);
        }
    }

    private Task PersistCacheEntryAsync(string imagePath, ImageTextCacheEntry entry) =>
        _ocrCacheStore.SaveAsync(new PersistedImageTextEntry(
            imagePath, entry.FileLength, entry.LastWriteUtcTicks, entry.OcrResult,
            entry.TranslatedText, entry.OverlayLines, entry.TargetLanguageCode,
            entry.TranslationProvider, entry.OverlayEnabled));

    private async Task TouchCacheSafelyAsync(string imagePath)
    {
        try { await _ocrCacheStore.TouchAsync(imagePath); }
        catch { }
    }

    private void UpdateTranslationButton()
    {
        if (OcrPanelButton is null) return;
        OcrPanelButton.Content = _continuousTranslationEnabled ? "번역 끄기" : "번역";
        if (TranslationStatusText is not null)
            TranslationStatusText.Text = _continuousTranslationEnabled
                ? (_translationOverlayVisible
                    ? "연속 번역이 켜져 있습니다. 다음 이미지도 자동 번역합니다."
                    : "연속 번역이 켜져 있습니다.")
                : "언어를 선택한 뒤 번역을 누르세요.";
    }

    private bool IsContinuousTranslationFolder() =>
        _continuousTranslationFolder is not null &&
        string.Equals(
            _continuousTranslationFolder,
            NormalizeFolderLocationKey(_currentFolder),
            StringComparison.OrdinalIgnoreCase);

    private void DisableContinuousTranslation()
    {
        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        _ocrCancellation = null;
        _continuousTranslationEnabled = false;
        _continuousTranslationFolder = null;
        ClearTranslationOverlay();
        _translatedOverlayLines = [];
        _translationOverlayVisible = false;
        OcrPanelButton.IsEnabled = true;
        UpdateTranslationButton();
    }

    private bool IsCurrentImage(string imagePath) =>
        _currentIndex >= 0 && _currentIndex < _images.Count &&
        string.Equals(_images[_currentIndex].FullPath, imagePath, StringComparison.OrdinalIgnoreCase);

    private void SetDetectedLanguageLabel(string languageTag)
    {
        var detected = _ocrService.GetAvailableLanguages()
            .FirstOrDefault(language => language.LanguageTag.Equals(languageTag, StringComparison.OrdinalIgnoreCase));
        var localLanguageName = languageTag switch
        {
            "zh-Hans" => "중국어",
            "ja" => "일본어",
            "ko" => "한국어",
            "en" => "영어",
            _ => languageTag
        };
        DetectedOcrLanguageText.Text = $"감지: {detected?.DisplayName ?? localLanguageName}";
    }

    private void RenderTranslationOverlay()
    {
        ClearTranslationOverlay();
        if (_viewerMode is ViewerMode.DualLeftToRight or ViewerMode.DualRightToLeft)
        {
            RenderDualTranslationOverlays();
            return;
        }
        if (_lastOcrResult is null || _translatedOverlayLines.Count == 0)
            return;

        var source = FlexibleImage.Source as BitmapSource ?? FitImage.Source as BitmapSource ?? OriginalImage.Source as BitmapSource;
        if (source is null) return;
        if (!TryGetRenderedImageBounds(source, out var imageBounds))
            imageBounds = new Rect(0, 0, TranslationOverlayCanvas.ActualWidth, TranslationOverlayCanvas.ActualHeight);

        var viewport = new Rect(0, 0, TranslationOverlayCanvas.ActualWidth, TranslationOverlayCanvas.ActualHeight);
        RenderTranslationOverlayEntry(_lastOcrResult, _translatedOverlayLines, source, imageBounds, viewport);
    }

    private void RenderDualTranslationOverlays()
    {
        if (_currentIndex < 0 || _currentIndex >= _images.Count) return;
        var companionIndex = FindImageIndex(_currentIndex, 1, 1);
        var currentPath = _images[_currentIndex].FullPath;
        var companionPath = companionIndex >= 0 ? _images[companionIndex].FullPath : null;
        var viewport = new Rect(0, 0, TranslationOverlayCanvas.ActualWidth, TranslationOverlayCanvas.ActualHeight);

        if (_viewerMode == ViewerMode.DualLeftToRight)
        {
            RenderCachedDualPage(currentPath, DualLeftImage, viewport);
            if (companionPath is not null) RenderCachedDualPage(companionPath, DualRightImage, viewport);
        }
        else
        {
            if (companionPath is not null) RenderCachedDualPage(companionPath, DualLeftImage, viewport);
            RenderCachedDualPage(currentPath, DualRightImage, viewport);
        }
    }

    private void RenderCachedDualPage(string imagePath, Image imageControl, Rect viewport)
    {
        if (!_imageTextCache.TryGetValue(imagePath, out var entry) || !entry.IsCurrent(imagePath)
            || !entry.OverlayEnabled || entry.OverlayLines.Count == 0
            || !string.Equals(entry.TranslationProvider, CurrentTranslationCacheProvider, StringComparison.OrdinalIgnoreCase)
            || imageControl.Source is not BitmapSource source
            || !TryGetRenderedImageBounds(imageControl, source, out var imageBounds))
            return;

        RenderTranslationOverlayEntry(entry.OcrResult, entry.OverlayLines, source, imageBounds, viewport);
    }

    private void RenderTranslationOverlayEntry(
        OcrTextResult ocrResult,
        IReadOnlyList<string> overlayLines,
        BitmapSource source,
        Rect imageBounds,
        Rect viewport)
    {
        var firstChildIndex = TranslationOverlayCanvas.Children.Count;

        var lineCount = Math.Min(ocrResult.Lines.Count, overlayLines.Count);
        var scaleX = imageBounds.Width / source.PixelWidth;
        var scaleY = imageBounds.Height / source.PixelHeight;
        var visibleImageBounds = Rect.Intersect(imageBounds, viewport);
        if (visibleImageBounds.IsEmpty) return;
        var placedBounds = new List<Rect>();
        var addedCount = 0;
        var omittedCount = Math.Max(0, overlayLines.Count - lineCount);
        for (var index = 0; index < lineCount; index++)
        {
            var region = ocrResult.Lines[index];
            var translated = overlayLines[index].Trim();
            if (translated.Length == 0) continue;

            var originalLeft = imageBounds.Left + region.X * scaleX;
            var originalTop = imageBounds.Top + region.Y * scaleY;
            var originalWidth = Math.Max(1, region.Width * scaleX);
            var originalHeight = Math.Max(1, region.Height * scaleY);
            var originalBounds = new Rect(originalLeft, originalTop, originalWidth, originalHeight);
            if (!viewport.IntersectsWith(originalBounds)) continue;

            var fontSize = Math.Clamp(region.TypicalLineHeight * scaleY * 0.72, 10, 16);
            var isVerticalSource = originalHeight > originalWidth * 1.6;
            var maxWidth = Math.Max(105, Math.Min(visibleImageBounds.Width * 0.52, 340));
            var preferredCharacters = Math.Clamp((int)Math.Ceiling(Math.Sqrt(translated.Length) * 2.5), 8, 26);
            if (isVerticalSource) preferredCharacters = Math.Max(preferredCharacters, 12);
            var width = Math.Clamp(
                Math.Max(originalWidth, preferredCharacters * fontSize * 0.66 + 12),
                Math.Min(95, maxWidth),
                maxWidth);
            var text = new TextBlock
            {
                Text = translated,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Medium,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            text.Measure(new Size(Math.Max(1, width - 8), double.PositiveInfinity));
            var height = Math.Max(originalHeight, Math.Ceiling(text.DesiredSize.Height) + 8);
            while (text.DesiredSize.Height + 8 > visibleImageBounds.Height && text.FontSize > 5)
            {
                text.FontSize -= 0.5;
                text.Measure(new Size(Math.Max(1, width - 8), double.PositiveInfinity));
            }
            if (text.DesiredSize.Height + 8 > visibleImageBounds.Height)
            {
                omittedCount++;
                continue;
            }
            height = Math.Min(
                visibleImageBounds.Height,
                Math.Max(originalHeight, Math.Ceiling(text.DesiredSize.Height) + 8));

            var left = originalLeft + originalWidth / 2 - width / 2;
            left = Math.Clamp(left, visibleImageBounds.Left, Math.Max(visibleImageBounds.Left, visibleImageBounds.Right - width));
            var top = Math.Clamp(originalTop, visibleImageBounds.Top, Math.Max(visibleImageBounds.Top, visibleImageBounds.Bottom - height));
            var placement = FindNonOverlappingPlacement(new Rect(left, top, width, height), visibleImageBounds, placedBounds);
            if (placement is null)
            {
                omittedCount++;
                continue;
            }
            left = placement.Value.Left;
            top = placement.Value.Top;
            var border = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(195, 18, 20, 24)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(190, 112, 183, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                Child = text
            };
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, top);
            TranslationOverlayCanvas.Children.Add(border);
            placedBounds.Add(new Rect(left - 3, top - 3, width + 6, height + 6));
            addedCount++;
        }

        // Never silently drop translated paragraphs. On an unusually dense page,
        // replace the individual boxes with one complete, measured overlay.
        if (addedCount == 0 || omittedCount > 0)
        {
            while (TranslationOverlayCanvas.Children.Count > firstChildIndex)
                TranslationOverlayCanvas.Children.RemoveAt(TranslationOverlayCanvas.Children.Count - 1);
            AddFallbackTranslationOverlay(imageBounds, viewport, overlayLines);
        }
    }

    private static Rect? FindNonOverlappingPlacement(Rect desired, Rect bounds, IReadOnlyList<Rect> occupied)
    {
        if (Fits(desired, bounds, occupied)) return desired;
        const double step = 10;
        var maximumDistance = Math.Max(bounds.Width, bounds.Height);
        for (var distance = step; distance <= maximumDistance; distance += step)
        {
            var offsets = new[]
            {
                new Vector(0, distance), new Vector(0, -distance),
                new Vector(-distance, 0), new Vector(distance, 0),
                new Vector(-distance, distance), new Vector(distance, distance),
                new Vector(-distance, -distance), new Vector(distance, -distance)
            };
            foreach (var offset in offsets)
            {
                var candidate = new Rect(desired.Location + offset, desired.Size);
                candidate.X = Math.Clamp(candidate.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - candidate.Width));
                candidate.Y = Math.Clamp(candidate.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - candidate.Height));
                if (Fits(candidate, bounds, occupied)) return candidate;
            }
        }

        // Dense pages may leave no nearby slot. Scan the remaining image area.
        for (var y = bounds.Top; y + desired.Height <= bounds.Bottom; y += step)
        for (var x = bounds.Left; x + desired.Width <= bounds.Right; x += step)
        {
            var candidate = new Rect(x, y, desired.Width, desired.Height);
            if (Fits(candidate, bounds, occupied)) return candidate;
        }
        return null;
    }

    private static bool Fits(Rect candidate, Rect bounds, IReadOnlyList<Rect> occupied) =>
        bounds.Contains(candidate) && occupied.All(existing => !existing.IntersectsWith(candidate));

    private void AddFallbackTranslationOverlay(
        Rect imageBounds,
        Rect viewport,
        IReadOnlyList<string> overlayLines)
    {
        var translated = string.Join(Environment.NewLine, overlayLines).Trim();
        if (translated.Length == 0 || viewport.Width <= 0 || viewport.Height <= 0) return;
        var visibleBounds = Rect.Intersect(imageBounds, viewport);
        if (visibleBounds.IsEmpty) return;
        const double outerMargin = 6;
        const double innerPadding = 10;
        var width = Math.Max(1, visibleBounds.Width - outerMargin * 2);
        var availableHeight = Math.Max(1, visibleBounds.Height - outerMargin * 2);
        var contentWidth = Math.Max(1, width - innerPadding * 2);
        var text = new TextBlock
        {
            Text = translated,
            Foreground = Brushes.White,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            Width = contentWidth
        };
        text.Measure(new Size(contentWidth, double.PositiveInfinity));
        while (text.DesiredSize.Height + innerPadding * 2 > availableHeight && text.FontSize > 5)
        {
            text.FontSize -= 0.5;
            text.Measure(new Size(contentWidth, double.PositiveInfinity));
        }
        var requiresFinalScaling = text.DesiredSize.Height + innerPadding * 2 > availableHeight;
        var height = requiresFinalScaling
            ? availableHeight
            : Math.Min(availableHeight, Math.Ceiling(text.DesiredSize.Height) + innerPadding * 2);
        UIElement content = text;
        if (requiresFinalScaling)
        {
            content = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = text
            };
        }
        var border = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(225, 18, 20, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(230, 112, 183, 255)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(innerPadding),
            Child = content
        };
        var left = visibleBounds.Left + outerMargin;
        var top = visibleBounds.Top + outerMargin;
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        TranslationOverlayCanvas.Children.Add(border);
    }

    private bool TryGetRenderedImageBounds(Image imageControl, BitmapSource source, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!imageControl.IsVisible || imageControl.ActualWidth <= 0 || imageControl.ActualHeight <= 0)
            return false;

        var controlBounds = imageControl.TransformToVisual(TranslationOverlayCanvas)
            .TransformBounds(new Rect(0, 0, imageControl.ActualWidth, imageControl.ActualHeight));
        var scale = Math.Min(controlBounds.Width / source.PixelWidth, controlBounds.Height / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        bounds = new Rect(
            controlBounds.Left + (controlBounds.Width - width) / 2,
            controlBounds.Top + (controlBounds.Height - height) / 2,
            width,
            height);
        return !bounds.IsEmpty;
    }

    private bool TryGetRenderedImageBounds(BitmapSource source, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (_viewerMode == ViewerMode.Original && OriginalImage.IsVisible)
        {
            bounds = OriginalImage.TransformToVisual(TranslationOverlayCanvas)
                .TransformBounds(new Rect(0, 0, OriginalImage.ActualWidth, OriginalImage.ActualHeight));
            return !bounds.IsEmpty;
        }
        if (_viewerMode == ViewerMode.Fit && FitImage.IsVisible)
        {
            bounds = FitImage.TransformToVisual(TranslationOverlayCanvas)
                .TransformBounds(new Rect(0, 0, FitImage.ActualWidth, FitImage.ActualHeight));
            return !bounds.IsEmpty;
        }
        if (!FlexibleImage.IsVisible || FlexibleImage.ActualWidth <= 0 || FlexibleImage.ActualHeight <= 0)
            return false;

        var controlBounds = FlexibleImage.TransformToVisual(TranslationOverlayCanvas)
            .TransformBounds(new Rect(0, 0, FlexibleImage.ActualWidth, FlexibleImage.ActualHeight));
        var scale = _viewerMode == ViewerMode.Fill
            ? Math.Max(controlBounds.Width / source.PixelWidth, controlBounds.Height / source.PixelHeight)
            : Math.Min(controlBounds.Width / source.PixelWidth, controlBounds.Height / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        bounds = new Rect(
            controlBounds.Left + (controlBounds.Width - width) / 2,
            controlBounds.Top + (controlBounds.Height - height) / 2,
            width,
            height);
        return true;
    }
}

public enum ViewerMode
{
    FitIncludingSmall,
    Original,
    Fit,
    Fill,
    DualLeftToRight,
    DualRightToLeft
}

internal sealed record TranslationOutput(
    string Text,
    IReadOnlyList<string> OverlayLines);

internal sealed record ImageTextCacheEntry(
    long FileLength,
    long LastWriteUtcTicks,
    OcrTextResult OcrResult,
    string TranslatedText,
    IReadOnlyList<string> OverlayLines,
    string TargetLanguageCode,
    string TranslationProvider,
    bool OverlayEnabled)
{
    public bool IsCurrent(string imagePath)
    {
        try
        {
            var info = new FileInfo(imagePath);
            return info.Exists && info.Length == FileLength && info.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;
        }
        catch
        {
            return false;
        }
    }
}
