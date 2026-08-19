using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CustomImageViewer.Services;

public static class AppLogService
{
    private static readonly object Sync = new();
    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomImageViewer", "logs");

    public static void Initialize()
    {
        Directory.CreateDirectory(LogFolder);
        foreach (var file in Directory.EnumerateFiles(LogFolder, "app-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30)) File.Delete(file);
            }
            catch { }
        }
        Info("Application", "프로그램을 시작했습니다.");
    }

    public static void Info(string area, string message) => Write("INFO", area, message, null);
    public static void Warning(string area, string message, Exception? exception = null) => Write("WARN", area, message, exception);
    public static void Error(string area, string message, Exception exception) => Write("ERROR", area, message, exception);

    public static string CreateDiagnosticReport(AppSettings settings)
    {
        var process = Process.GetCurrentProcess();
        var builder = new StringBuilder();
        builder.AppendLine("TagSeeker 진단 정보");
        builder.AppendLine($"생성 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"앱 버전: {Assembly.GetExecutingAssembly().GetName().Version}");
        builder.AppendLine($"운영체제: {RuntimeInformation.OSDescription}");
        builder.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"프로세스: {RuntimeInformation.ProcessArchitecture} / 64비트={Environment.Is64BitProcess}");
        builder.AppendLine($"CPU 논리 코어: {Environment.ProcessorCount}");
        builder.AppendLine($"프로세스 메모리: {process.WorkingSet64 / (1024d * 1024):N1}MB");
        builder.AppendLine($"마지막 폴더: {settings.LastFolderPath ?? "(없음)"}");
        builder.AppendLine($"미리보기 페이지 크기: {settings.ExplorerPageSize}");
        builder.AppendLine($"미리보기 캐시 제한: {settings.ThumbnailCacheMaxMegabytes}MB");
        builder.AppendLine($"휠 속도: {settings.MouseWheelSpeedMultiplier}배");
        builder.AppendLine($"정렬: 필드={settings.ExplorerSortField}, 내림차순={settings.ExplorerSortDescending}, 폴더 우선={settings.ExplorerFoldersFirst}");
        builder.AppendLine($"번역 결과 언어: {settings.TargetLanguageCode}");
        builder.AppendLine($"자동 태그 백업: {settings.TagAutoBackupEnabled}, 보관={settings.TagBackupRetentionCount}");
        builder.AppendLine($"자동 업데이트 확인: {settings.AutomaticUpdateCheckEnabled}");
        builder.AppendLine($"활성 태그 세트 ID: {settings.ActiveTagSetId}");
        builder.AppendLine($"접두어 형식: {settings.PrefixPatterns?.Count ?? 0}개");
        builder.AppendLine($"로그 폴더: {LogFolder}");
        return builder.ToString();
    }

    private static void Write(string level, string area, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, $"app-{DateTime.Now:yyyy-MM-dd}.log");
            var text = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(level).Append("] [").Append(area).Append("] [T")
                .Append(Environment.CurrentManagedThreadId).Append("] ").AppendLine(message);
            if (exception is not null) text.AppendLine(exception.ToString());
            lock (Sync) File.AppendAllText(path, text.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
