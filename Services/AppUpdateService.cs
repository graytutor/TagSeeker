using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomImageViewer.Services;

public sealed record AppUpdateInfo(
    Version Version,
    string VersionText,
    string ReleasePageUrl,
    string DownloadUrl,
    string AssetName,
    long AssetSize,
    string? Sha256Digest);

public sealed class AppUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/graytutor/TagSeeker/releases/latest";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public string CurrentVersionText =>
        $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 릴리스 정보를 읽지 못했습니다.");

        if (!TryParseVersion(release.TagName, out var releaseVersion))
            throw new InvalidDataException($"릴리스 버전을 해석하지 못했습니다: {release.TagName}");

        if (releaseVersion <= NormalizeVersion(CurrentVersion)) return null;

        var expectedPrefix = $"TagSeeker-{releaseVersion.Major}.{releaseVersion.Minor}.{releaseVersion.Build}-win-x64-portable";
        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && candidate.Name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            throw new InvalidDataException("이 릴리스에 Windows x64 포터블 ZIP이 없습니다.");

        return new AppUpdateInfo(
            releaseVersion,
            $"{releaseVersion.Major}.{releaseVersion.Minor}.{releaseVersion.Build}",
            release.HtmlUrl,
            asset.BrowserDownloadUrl,
            asset.Name,
            asset.Size,
            ParseSha256Digest(asset.Digest));
    }

    public async Task DownloadAndPrepareAsync(
        AppUpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInstallFolderIsWritable();
        var updateRoot = Path.Combine(
            Path.GetTempPath(),
            $"TagSeeker-Update-{update.VersionText}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(updateRoot, update.AssetName);
        var stagingPath = Path.Combine(updateRoot, "staging");
        Directory.CreateDirectory(stagingPath);

        try
        {
            using var response = await HttpClient.GetAsync(
                update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var expectedSize = response.Content.Headers.ContentLength ?? update.AssetSize;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 128];
                long received = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    if (expectedSize > 0) progress?.Report(received / (double)expectedSize * 0.9);
                }
            }

            if (update.AssetSize > 0 && new FileInfo(zipPath).Length != update.AssetSize)
                throw new InvalidDataException("다운로드한 파일 크기가 릴리스 정보와 일치하지 않습니다.");
            if (update.Sha256Digest is { } expectedDigest)
            {
                await using var zipStream = File.OpenRead(zipPath);
                var actualDigest = Convert.ToHexString(await SHA256.HashDataAsync(zipStream, cancellationToken));
                if (!actualDigest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("다운로드한 ZIP의 SHA-256 값이 일치하지 않습니다.");
            }

            await ExtractValidatedAsync(zipPath, stagingPath, update.Version, cancellationToken);
            progress?.Report(0.97);
            var scriptPath = await WriteUpdaterScriptAsync(updateRoot, stagingPath, cancellationToken);
            StartUpdater(scriptPath);
            progress?.Report(1);
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }
    }

    private static async Task ExtractValidatedAsync(
        string zipPath, string stagingPath, Version expectedVersion, CancellationToken cancellationToken)
    {
        var stagingRoot = Path.GetFullPath(stagingPath) + Path.DirectorySeparatorChar;
        var foundExecutable = false;
        string? applicationAssemblyPath = null;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            var destination = Path.GetFullPath(Path.Combine(stagingPath, relativePath));
            if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP에 안전하지 않은 경로가 포함되어 있습니다.");

            var firstPart = relativePath.Split(Path.DirectorySeparatorChar, 2)[0];
            if (firstPart.Equals("TranslationModel", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            if (relativePath.Equals("TagSeeker.exe", StringComparison.OrdinalIgnoreCase))
                foundExecutable = true;
            else if (relativePath.Equals("TagSeeker.dll", StringComparison.OrdinalIgnoreCase))
                applicationAssemblyPath = destination;
        }

        if (!foundExecutable)
            throw new InvalidDataException("ZIP 루트에서 TagSeeker.exe를 찾지 못했습니다.");
        if (applicationAssemblyPath is null)
            throw new InvalidDataException("ZIP 루트에서 TagSeeker.dll을 찾지 못했습니다.");
        var archiveVersion = AssemblyName.GetAssemblyName(applicationAssemblyPath).Version;
        if (archiveVersion is null || NormalizeVersion(archiveVersion) != NormalizeVersion(expectedVersion))
            throw new InvalidDataException(
                $"ZIP의 프로그램 버전이 예상 버전과 다릅니다. 예상: {expectedVersion}, 실제: {archiveVersion}");
    }

    private static async Task<string> WriteUpdaterScriptAsync(
        string updateRoot, string stagingPath, CancellationToken cancellationToken)
    {
        var targetPath = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var executablePath = Path.Combine(targetPath, "TagSeeker.exe");
        var logPath = Path.Combine(AppLogService.LogFolder, "update.log");
        Directory.CreateDirectory(AppLogService.LogFolder);
        var scriptPath = Path.Combine(updateRoot, "apply-update.ps1");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $processId = {{Environment.ProcessId}}
            $staging = '{{EscapePowerShellLiteral(stagingPath)}}'
            $target = '{{EscapePowerShellLiteral(targetPath)}}'
            $executable = '{{EscapePowerShellLiteral(executablePath)}}'
            $updateRoot = '{{EscapePowerShellLiteral(updateRoot)}}'
            $backup = Join-Path $updateRoot 'backup'
            $log = '{{EscapePowerShellLiteral(logPath)}}'
            $newFiles = [System.Collections.Generic.List[string]]::new()
            try {
                try { Wait-Process -Id $processId -Timeout 120 -ErrorAction SilentlyContinue } catch {}
                Start-Sleep -Milliseconds 700
                New-Item -ItemType Directory -Path $backup -Force | Out-Null
                foreach ($source in Get-ChildItem -LiteralPath $staging -File -Recurse -Force) {
                    $relative = $source.FullName.Substring($staging.Length).TrimStart([char[]]'\/')
                    if ($relative.Split([char[]]'\/')[0] -eq 'TranslationModel') { continue }
                    $destination = Join-Path $target $relative
                    $destinationFolder = Split-Path -Parent $destination
                    New-Item -ItemType Directory -Path $destinationFolder -Force | Out-Null
                    if (Test-Path -LiteralPath $destination -PathType Leaf) {
                        $backupFile = Join-Path $backup $relative
                        New-Item -ItemType Directory -Path (Split-Path -Parent $backupFile) -Force | Out-Null
                        Copy-Item -LiteralPath $destination -Destination $backupFile -Force
                    } else {
                        $newFiles.Add($destination)
                    }
                    $copied = $false
                    for ($attempt = 1; $attempt -le 30 -and -not $copied; $attempt++) {
                        try {
                            Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
                            $copied = $true
                        } catch {
                            if ($attempt -eq 30) { throw }
                            Start-Sleep -Seconds 1
                        }
                    }
                }
                Start-Process -FilePath $executable -WorkingDirectory $target
            } catch {
                Add-Content -LiteralPath $log -Value ("{0:u} 업데이트 실패: {1}" -f (Get-Date), $_.Exception.ToString())
                try {
                    if (Test-Path -LiteralPath $backup) {
                        Get-ChildItem -LiteralPath $backup -File -Recurse -Force | ForEach-Object {
                            $relative = $_.FullName.Substring($backup.Length).TrimStart([char[]]'\/')
                            $destination = Join-Path $target $relative
                            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
                        }
                    }
                    foreach ($newFile in $newFiles) {
                        Remove-Item -LiteralPath $newFile -Force -ErrorAction SilentlyContinue
                    }
                } catch {
                    Add-Content -LiteralPath $log -Value ("{0:u} 업데이트 복구 실패: {1}" -f (Get-Date), $_.Exception.ToString())
                }
                try { Start-Process -FilePath $executable -WorkingDirectory $target } catch {}
            } finally {
                Start-Sleep -Seconds 2
                Remove-Item -LiteralPath $updateRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), cancellationToken);
        return scriptPath;
    }

    private static void StartUpdater(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("업데이트 적용 프로세스를 시작하지 못했습니다.");
    }

    private static void EnsureInstallFolderIsWritable()
    {
        var marker = Path.Combine(AppContext.BaseDirectory, $".tagseeker-update-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(marker, string.Empty);
            File.Delete(marker);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                "현재 설치 폴더에 파일을 쓸 수 없어 자동 업데이트할 수 없습니다. " +
                "쓰기 가능한 폴더로 TagSeeker를 옮기거나 관리자 권한으로 실행하세요.", ex);
        }
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var text = value.Trim().TrimStart('v', 'V');
        var suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0) text = text[..suffix];
        if (Version.TryParse(text, out var parsed))
        {
            version = NormalizeVersion(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static string? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        return digest?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? digest[prefix.Length..]
            : null;
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TagSeeker-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
