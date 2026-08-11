using System.IO;
using System.Text.Json;

namespace CustomImageViewer.Services;

public sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomImageViewer");
        Directory.CreateDirectory(dataFolder);
        _settingsPath = Path.Combine(dataFolder, "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AppSettings();
            var json = await File.ReadAllTextAsync(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_settingsPath, json);
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}

public sealed class AppSettings
{
    public string? LastFolderPath { get; set; }
    public int ViewerMode { get; set; }
    public string TargetLanguageCode { get; set; } = "ko";
    public bool ExitOnEscape { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public int ExplorerSortField { get; set; }
    public bool ExplorerSortDescending { get; set; }
    public bool ExplorerFoldersFirst { get; set; } = true;
    public int ExplorerPageSize { get; set; } = 300;
    public int MouseWheelSpeedMultiplier { get; set; } = 3;
    public double ExplorerThumbnailSize { get; set; } = 190;
    public bool HideAuthorPrefix { get; set; }
    public int ThumbnailCacheMaxMegabytes { get; set; } = 2048;
    public bool TagAutoBackupEnabled { get; set; } = true;
    public int TagBackupRetentionCount { get; set; } = 10;
    public long LastTagBackupUtcTicks { get; set; }
}
