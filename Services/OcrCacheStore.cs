using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace CustomImageViewer.Services;

public sealed class OcrCacheStore
{
    private const int CurrentOcrLayoutVersion = 4;
    public const long MaximumCacheBytes = 250L * 1024 * 1024;
    public static readonly TimeSpan MaximumUnusedAge = TimeSpan.FromDays(180);
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new();

    public OcrCacheStore()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomImageViewer");
        Directory.CreateDirectory(dataFolder);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataFolder, "ocr-cache.db"),
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
            CREATE TABLE IF NOT EXISTS ImageTextCache (
                PathKey TEXT PRIMARY KEY,
                OriginalPath TEXT NOT NULL,
                FileLength INTEGER NOT NULL,
                LastWriteUtcTicks INTEGER NOT NULL,
                OcrResultJson TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                OverlayLinesJson TEXT NOT NULL,
                TargetLanguageCode TEXT NOT NULL,
                TranslationProvider TEXT NOT NULL DEFAULT '',
                OcrLayoutVersion INTEGER NOT NULL DEFAULT 1,
                OverlayEnabled INTEGER NOT NULL,
                UpdatedUtcTicks INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "PRAGMA table_info(ImageTextCache);";
        await using (var reader = await columnCommand.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        if (!columns.Contains("TranslationProvider"))
        {
            var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE ImageTextCache ADD COLUMN TranslationProvider TEXT NOT NULL DEFAULT '';";
            await migrate.ExecuteNonQueryAsync();
        }
        if (!columns.Contains("OcrLayoutVersion"))
        {
            var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE ImageTextCache ADD COLUMN OcrLayoutVersion INTEGER NOT NULL DEFAULT 1;";
            await migrate.ExecuteNonQueryAsync();
        }
    }

    public async Task<IReadOnlyList<PersistedImageTextEntry>> LoadAllAsync()
    {
        var entries = new List<PersistedImageTextEntry>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OriginalPath, FileLength, LastWriteUtcTicks, OcrResultJson,
                   TranslatedText, OverlayLinesJson, TargetLanguageCode, TranslationProvider, OverlayEnabled
            FROM ImageTextCache
            WHERE OcrLayoutVersion = $layoutVersion;
            """;
        command.Parameters.AddWithValue("$layoutVersion", CurrentOcrLayoutVersion);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            try
            {
                var ocr = JsonSerializer.Deserialize<OcrTextResult>(reader.GetString(3), JsonOptions);
                var lines = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOptions) ?? [];
                if (ocr is null) continue;
                entries.Add(new PersistedImageTextEntry(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), ocr,
                    reader.GetString(4), lines, reader.GetString(6), reader.GetString(7), reader.GetInt64(8) != 0));
            }
            catch
            {
                // Ignore a single old or damaged cache row; the image can be recognized again.
            }
        }
        return entries;
    }

    public async Task CleanupAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var removedRows = 0;

        var removeOld = connection.CreateCommand();
        removeOld.CommandText = "DELETE FROM ImageTextCache WHERE UpdatedUtcTicks < $cutoff;";
        removeOld.Parameters.AddWithValue("$cutoff", (DateTime.UtcNow - MaximumUnusedAge).Ticks);
        removedRows += await removeOld.ExecuteNonQueryAsync();

        var knownPaths = new List<(string Key, string Path)>();
        var readPaths = connection.CreateCommand();
        readPaths.CommandText = "SELECT PathKey, OriginalPath FROM ImageTextCache;";
        await using (var reader = await readPaths.ExecuteReaderAsync())
            while (await reader.ReadAsync()) knownPaths.Add((reader.GetString(0), reader.GetString(1)));

        foreach (var item in knownPaths.Where(item => IsDefinitelyMissing(item.Path)))
        {
            var removeMissing = connection.CreateCommand();
            removeMissing.CommandText = "DELETE FROM ImageTextCache WHERE PathKey = $key;";
            removeMissing.Parameters.AddWithValue("$key", item.Key);
            removedRows += await removeMissing.ExecuteNonQueryAsync();
        }

        var databaseBytes = await GetDatabaseBytesAsync(connection);
        var needsVacuum = removedRows > 0 || databaseBytes > MaximumCacheBytes;
        if (databaseBytes > MaximumCacheBytes)
        {
            var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM ImageTextCache;";
            var count = Convert.ToInt64(await countCommand.ExecuteScalarAsync());
            var keepRatio = MaximumCacheBytes * 0.85 / databaseBytes;
            var keepCount = Math.Max(0, (long)Math.Floor(count * keepRatio));
            var deleteCount = Math.Max(0, count - keepCount);
            if (deleteCount > 0)
            {
                var trim = connection.CreateCommand();
                trim.CommandText = """
                    DELETE FROM ImageTextCache
                    WHERE PathKey IN (
                        SELECT PathKey FROM ImageTextCache
                        ORDER BY UpdatedUtcTicks ASC
                        LIMIT $deleteCount
                    );
                    """;
                trim.Parameters.AddWithValue("$deleteCount", deleteCount);
                removedRows += await trim.ExecuteNonQueryAsync();
            }
        }

        if (needsVacuum)
        {
            var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
            var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync();
        }
    }

    public async Task TouchAsync(string imagePath)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE ImageTextCache SET UpdatedUtcTicks = $now WHERE PathKey = $key;";
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("$key", NormalizePath(imagePath));
        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveAsync(PersistedImageTextEntry entry)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ImageTextCache(
                PathKey, OriginalPath, FileLength, LastWriteUtcTicks, OcrResultJson,
                TranslatedText, OverlayLinesJson, TargetLanguageCode, TranslationProvider,
                OcrLayoutVersion, OverlayEnabled, UpdatedUtcTicks)
            VALUES($key, $path, $length, $modified, $ocr, $translation, $lines, $target, $provider,
                   $layoutVersion, $enabled, $updated)
            ON CONFLICT(PathKey) DO UPDATE SET
                OriginalPath = excluded.OriginalPath,
                FileLength = excluded.FileLength,
                LastWriteUtcTicks = excluded.LastWriteUtcTicks,
                OcrResultJson = excluded.OcrResultJson,
                TranslatedText = excluded.TranslatedText,
                OverlayLinesJson = excluded.OverlayLinesJson,
                TargetLanguageCode = excluded.TargetLanguageCode,
                TranslationProvider = excluded.TranslationProvider,
                OcrLayoutVersion = excluded.OcrLayoutVersion,
                OverlayEnabled = excluded.OverlayEnabled,
                UpdatedUtcTicks = excluded.UpdatedUtcTicks;
            """;
        command.Parameters.AddWithValue("$key", NormalizePath(entry.ImagePath));
        command.Parameters.AddWithValue("$path", Path.GetFullPath(entry.ImagePath));
        command.Parameters.AddWithValue("$length", entry.FileLength);
        command.Parameters.AddWithValue("$modified", entry.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$ocr", JsonSerializer.Serialize(entry.OcrResult, JsonOptions));
        command.Parameters.AddWithValue("$translation", entry.TranslatedText);
        command.Parameters.AddWithValue("$lines", JsonSerializer.Serialize(entry.OverlayLines, JsonOptions));
        command.Parameters.AddWithValue("$target", entry.TargetLanguageCode);
        command.Parameters.AddWithValue("$provider", entry.TranslationProvider);
        command.Parameters.AddWithValue("$layoutVersion", CurrentOcrLayoutVersion);
        command.Parameters.AddWithValue("$enabled", entry.OverlayEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.Ticks);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string imagePath)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ImageTextCache WHERE PathKey = $key;";
        command.Parameters.AddWithValue("$key", NormalizePath(imagePath));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> GetDatabaseBytesAsync(SqliteConnection connection)
    {
        var pageCountCommand = connection.CreateCommand();
        pageCountCommand.CommandText = "PRAGMA page_count;";
        var pageCount = Convert.ToInt64(await pageCountCommand.ExecuteScalarAsync());
        var pageSizeCommand = connection.CreateCommand();
        pageSizeCommand.CommandText = "PRAGMA page_size;";
        var pageSize = Convert.ToInt64(await pageSizeCommand.ExecuteScalarAsync());
        return pageCount * pageSize;
    }

    private static bool IsDefinitelyMissing(string path)
    {
        try
        {
            if (File.Exists(path)) return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\")) return false;
            var drive = new DriveInfo(root);
            return drive.IsReady;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).ToUpperInvariant();
}

public sealed record PersistedImageTextEntry(
    string ImagePath,
    long FileLength,
    long LastWriteUtcTicks,
    OcrTextResult OcrResult,
    string TranslatedText,
    IReadOnlyList<string> OverlayLines,
    string TargetLanguageCode,
    string TranslationProvider,
    bool OverlayEnabled);
