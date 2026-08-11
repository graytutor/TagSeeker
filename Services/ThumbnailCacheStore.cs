using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace CustomImageViewer.Services;

public sealed class ThumbnailCacheStore
{
    private readonly string _cacheFolder;
    private readonly string _connectionString;

    public ThumbnailCacheStore()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomImageViewer");
        _cacheFolder = Path.Combine(dataFolder, "thumbnail-cache");
        Directory.CreateDirectory(_cacheFolder);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataFolder, "thumbnail-cache.db"),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS ThumbnailCache (
                ItemPathKey TEXT PRIMARY KEY,
                ItemPath TEXT NOT NULL,
                ItemLastWriteUtcTicks INTEGER NOT NULL,
                SourcePath TEXT NOT NULL,
                SourceLength INTEGER NOT NULL,
                SourceLastWriteUtcTicks INTEGER NOT NULL,
                CacheFileName TEXT NOT NULL,
                CacheSizeBytes INTEGER NOT NULL,
                LastAccessUtcTicks INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ThumbnailCache_LastAccess
                ON ThumbnailCache(LastAccessUtcTicks);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<BitmapSource?> TryLoadAsync(string itemPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CacheEntry? entry = null;
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ItemPath, ItemLastWriteUtcTicks, SourcePath, SourceLength,
                       SourceLastWriteUtcTicks, CacheFileName
                FROM ThumbnailCache
                WHERE ItemPathKey = $key;
                """;
            command.Parameters.AddWithValue("$key", NormalizePath(itemPath));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                entry = new CacheEntry(
                    reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5));
        }

        if (entry is null) return null;
        var cachePath = Path.Combine(_cacheFolder, entry.CacheFileName);
        if (!IsEntryCurrent(entry, cachePath))
        {
            await RemoveEntryAsync(itemPath, entry.CacheFileName);
            return null;
        }

        try
        {
            var bitmap = await Task.Run(() => LoadBitmap(cachePath), cancellationToken);
            await TouchAsync(itemPath);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await RemoveEntryAsync(itemPath, entry.CacheFileName);
            return null;
        }
    }

    public async Task SaveAsync(
        string itemPath,
        string sourcePath,
        BitmapSource thumbnail,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath) || (!File.Exists(itemPath) && !Directory.Exists(itemPath))) return;

        var sourceInfo = new FileInfo(sourcePath);
        var itemLastWrite = GetItemLastWriteUtc(itemPath).Ticks;
        var pathKey = NormalizePath(itemPath);
        var cacheFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pathKey))) + ".jpg";
        var cachePath = Path.Combine(_cacheFolder, cacheFileName);
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await Task.Run(() => SaveJpeg(thumbnail, temporaryPath), cancellationToken);
            File.Move(temporaryPath, cachePath, overwrite: true);
            var cacheSize = new FileInfo(cachePath).Length;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ThumbnailCache(
                    ItemPathKey, ItemPath, ItemLastWriteUtcTicks, SourcePath, SourceLength,
                    SourceLastWriteUtcTicks, CacheFileName, CacheSizeBytes, LastAccessUtcTicks)
                VALUES($key, $itemPath, $itemWrite, $sourcePath, $sourceLength,
                       $sourceWrite, $fileName, $cacheSize, $access)
                ON CONFLICT(ItemPathKey) DO UPDATE SET
                    ItemPath = excluded.ItemPath,
                    ItemLastWriteUtcTicks = excluded.ItemLastWriteUtcTicks,
                    SourcePath = excluded.SourcePath,
                    SourceLength = excluded.SourceLength,
                    SourceLastWriteUtcTicks = excluded.SourceLastWriteUtcTicks,
                    CacheFileName = excluded.CacheFileName,
                    CacheSizeBytes = excluded.CacheSizeBytes,
                    LastAccessUtcTicks = excluded.LastAccessUtcTicks;
                """;
            command.Parameters.AddWithValue("$key", pathKey);
            command.Parameters.AddWithValue("$itemPath", Path.GetFullPath(itemPath));
            command.Parameters.AddWithValue("$itemWrite", itemLastWrite);
            command.Parameters.AddWithValue("$sourcePath", sourceInfo.FullName);
            command.Parameters.AddWithValue("$sourceLength", sourceInfo.Length);
            command.Parameters.AddWithValue("$sourceWrite", sourceInfo.LastWriteTimeUtc.Ticks);
            command.Parameters.AddWithValue("$fileName", cacheFileName);
            command.Parameters.AddWithValue("$cacheSize", cacheSize);
            command.Parameters.AddWithValue("$access", DateTime.UtcNow.Ticks);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
    }

    public async Task CleanupAsync(int maximumMegabytes)
    {
        var maximumBytes = Math.Clamp(maximumMegabytes, 128, 10240) * 1024L * 1024L;
        var oldestAllowed = DateTime.UtcNow.AddDays(-180).Ticks;
        var entries = new List<(string Key, string ItemPath, string FileName, long Size, long Access)>();

        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ItemPathKey, ItemPath, CacheFileName, CacheSizeBytes, LastAccessUtcTicks
                FROM ThumbnailCache ORDER BY LastAccessUtcTicks DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                entries.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), reader.GetInt64(4)));
        }

        long retainedBytes = 0;
        foreach (var entry in entries)
        {
            var cachePath = Path.Combine(_cacheFolder, entry.FileName);
            var keep = entry.Access >= oldestAllowed
                       && (File.Exists(entry.ItemPath) || Directory.Exists(entry.ItemPath))
                       && File.Exists(cachePath)
                       && retainedBytes + entry.Size <= maximumBytes;
            if (keep)
            {
                retainedBytes += entry.Size;
                continue;
            }
            await RemoveEntryByKeyAsync(entry.Key, entry.FileName);
        }
    }

    public async Task ClearAsync()
    {
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ThumbnailCache;";
            await command.ExecuteNonQueryAsync();
        }

        foreach (var file in Directory.EnumerateFiles(_cacheFolder))
        {
            try { File.Delete(file); }
            catch { }
        }
    }

    public async Task<(int Count, long SizeBytes)> GetStatsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(CacheSizeBytes), 0) FROM ThumbnailCache;";
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetInt64(1)) : (0, 0);
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveJpeg(BitmapSource bitmap, string path)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static bool IsEntryCurrent(CacheEntry entry, string cachePath)
    {
        if (!File.Exists(cachePath) || !File.Exists(entry.SourcePath)) return false;
        if (!File.Exists(entry.ItemPath) && !Directory.Exists(entry.ItemPath)) return false;
        var source = new FileInfo(entry.SourcePath);
        return source.Length == entry.SourceLength
               && source.LastWriteTimeUtc.Ticks == entry.SourceLastWriteUtcTicks
               && GetItemLastWriteUtc(entry.ItemPath).Ticks == entry.ItemLastWriteUtcTicks;
    }

    private async Task TouchAsync(string itemPath)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE ThumbnailCache SET LastAccessUtcTicks = $access WHERE ItemPathKey = $key;";
        command.Parameters.AddWithValue("$access", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("$key", NormalizePath(itemPath));
        await command.ExecuteNonQueryAsync();
    }

    private Task RemoveEntryAsync(string itemPath, string fileName) =>
        RemoveEntryByKeyAsync(NormalizePath(itemPath), fileName);

    private async Task RemoveEntryByKeyAsync(string pathKey, string fileName)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ThumbnailCache WHERE ItemPathKey = $key;";
        command.Parameters.AddWithValue("$key", pathKey);
        await command.ExecuteNonQueryAsync();
        try
        {
            var path = Path.Combine(_cacheFolder, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static DateTime GetItemLastWriteUtc(string path) =>
        Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

    private sealed record CacheEntry(
        string ItemPath,
        long ItemLastWriteUtcTicks,
        string SourcePath,
        long SourceLength,
        long SourceLastWriteUtcTicks,
        string CacheFileName);
}
