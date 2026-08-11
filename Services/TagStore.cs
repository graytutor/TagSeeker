using Microsoft.Data.Sqlite;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CustomImageViewer.Services;

public sealed class TagStore
{
    public const long DefaultTagSetId = 1;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly string _backupFolder;
    public long ActiveTagSetId { get; set; } = DefaultTagSetId;

    public TagStore()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomImageViewer");
        Directory.CreateDirectory(dataFolder);
        _databasePath = Path.Combine(dataFolder, "tags.db");
        _backupFolder = Path.Combine(dataFolder, "tag-backups");
        Directory.CreateDirectory(_backupFolder);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Resources (
                PathKey TEXT PRIMARY KEY,
                OriginalPath TEXT NOT NULL,
                IsDirectory INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TagSets (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                NormalizedName TEXT NOT NULL UNIQUE
            );
            INSERT OR IGNORE INTO TagSets(Id, Name, NormalizedName) VALUES(1, '기본', '기본');
            CREATE TABLE IF NOT EXISTS Tags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                NormalizedName TEXT NOT NULL,
                TagSetId INTEGER NOT NULL DEFAULT 1,
                UNIQUE(TagSetId, NormalizedName),
                FOREIGN KEY (TagSetId) REFERENCES TagSets(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS ResourceTags (
                ResourcePathKey TEXT NOT NULL,
                TagId INTEGER NOT NULL,
                PRIMARY KEY (ResourcePathKey, TagId),
                FOREIGN KEY (ResourcePathKey) REFERENCES Resources(PathKey) ON DELETE CASCADE,
                FOREIGN KEY (TagId) REFERENCES Tags(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_ResourceTags_TagId ON ResourceTags(TagId);
            """;
        await command.ExecuteNonQueryAsync();

        var columnCheck = connection.CreateCommand();
        columnCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Tags') WHERE name = 'TagSetId';";
        if (Convert.ToInt32(await columnCheck.ExecuteScalarAsync()) == 0)
        {
            var migrationBackup = Path.Combine(
                _backupFolder,
                $"tags-before-tagsets-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            using (var backupConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = migrationBackup,
                       Mode = SqliteOpenMode.ReadWriteCreate
                   }.ToString()))
            {
                backupConnection.Open();
                connection.BackupDatabase(backupConnection);
            }

            var migrate = connection.CreateCommand();
            migrate.CommandText = """
                PRAGMA foreign_keys = OFF;
                BEGIN IMMEDIATE;
                CREATE TABLE Tags_New (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    NormalizedName TEXT NOT NULL,
                    TagSetId INTEGER NOT NULL DEFAULT 1,
                    UNIQUE(TagSetId, NormalizedName),
                    FOREIGN KEY (TagSetId) REFERENCES TagSets(Id) ON DELETE CASCADE
                );
                INSERT INTO Tags_New(Id, Name, NormalizedName, TagSetId)
                    SELECT Id, Name, NormalizedName, 1 FROM Tags;
                DROP TABLE Tags;
                ALTER TABLE Tags_New RENAME TO Tags;
                COMMIT;
                PRAGMA foreign_keys = ON;
                """;
            await migrate.ExecuteNonQueryAsync();
        }

        var indexes = connection.CreateCommand();
        indexes.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_ResourceTags_TagId ON ResourceTags(TagId);
            CREATE INDEX IF NOT EXISTS IX_Tags_TagSetId ON Tags(TagSetId);
            """;
        await indexes.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<TagSetSummary>> GetTagSetsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts.Id, ts.Name, COUNT(t.Id)
            FROM TagSets ts
            LEFT JOIN Tags t ON t.TagSetId = ts.Id
            GROUP BY ts.Id, ts.Name
            ORDER BY ts.Name COLLATE NOCASE;
            """;
        var result = new List<TagSetSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new TagSetSummary(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return result;
    }

    public async Task<long> CreateTagSetAsync(string name)
    {
        var cleanName = CleanTagSetName(name);
        if (cleanName.Length == 0) throw new ArgumentException("태그 세트 이름을 입력하세요.", nameof(name));

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TagSets(Name, NormalizedName) VALUES($name, $normalized)
            ON CONFLICT(NormalizedName) DO NOTHING
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$name", cleanName);
        command.Parameters.AddWithValue("$normalized", NormalizeTagSetName(cleanName));
        var result = await command.ExecuteScalarAsync();
        if (result is null)
            throw new InvalidOperationException("같은 이름의 태그 세트가 이미 있습니다.");
        return Convert.ToInt64(result);
    }

    public async Task RenameTagSetAsync(long tagSetId, string newName)
    {
        var cleanName = CleanTagSetName(newName);
        if (cleanName.Length == 0) throw new ArgumentException("태그 세트 이름을 입력하세요.", nameof(newName));

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE TagSets SET Name = $name, NormalizedName = $normalized WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", cleanName);
        command.Parameters.AddWithValue("$normalized", NormalizeTagSetName(cleanName));
        command.Parameters.AddWithValue("$id", tagSetId);
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("같은 이름의 태그 세트가 이미 있습니다.", ex);
        }
    }

    public async Task DeleteTagSetAsync(long tagSetId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM TagSets;";
        if (Convert.ToInt32(await count.ExecuteScalarAsync()) <= 1)
            throw new InvalidOperationException("마지막 태그 세트는 삭제할 수 없습니다.");

        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM TagSets WHERE Id = $id;";
        delete.Parameters.AddWithValue("$id", tagSetId);
        await delete.ExecuteNonQueryAsync();

        var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = """
            DELETE FROM Resources
            WHERE NOT EXISTS (SELECT 1 FROM ResourceTags WHERE ResourcePathKey = Resources.PathKey);
            """;
        await cleanup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(string path)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Name
            FROM ResourceTags rt
            JOIN Tags t ON t.Id = rt.TagId
            WHERE rt.ResourcePathKey = $pathKey AND t.TagSetId = $tagSetId
            ORDER BY t.Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$pathKey", NormalizePath(path));
        command.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetTagsForPathsAsync(
        IEnumerable<string> paths)
    {
        var requestedPaths = paths
            .Select(path => new { Original = path, Key = NormalizePath(path) })
            .DistinctBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
        var originalByKey = requestedPaths.ToDictionary(item => item.Key, item => item.Original, StringComparer.Ordinal);
        var tagsByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Keep well below SQLite's parameter limit while reusing one connection.
        foreach (var chunk in requestedPaths.Chunk(400))
        {
            var command = connection.CreateCommand();
            var parameters = chunk.Select((_, index) => $"$path{index}").ToArray();
            command.CommandText = $"""
                SELECT rt.ResourcePathKey, t.Name
                FROM ResourceTags rt
                JOIN Tags t ON t.Id = rt.TagId
                WHERE rt.ResourcePathKey IN ({string.Join(", ", parameters)})
                  AND t.TagSetId = $tagSetId
                ORDER BY rt.ResourcePathKey, t.Name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
            for (var index = 0; index < chunk.Length; index++)
                command.Parameters.AddWithValue(parameters[index], chunk[index].Key);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var pathKey = reader.GetString(0);
                if (!originalByKey.TryGetValue(pathKey, out var originalPath)) continue;
                if (!tagsByPath.TryGetValue(originalPath, out var tags))
                {
                    tags = [];
                    tagsByPath[originalPath] = tags;
                }
                tags.Add(reader.GetString(1));
            }
        }

        return tagsByPath.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<TagSummary>> GetAllTagsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Name, COUNT(rt.ResourcePathKey)
            FROM Tags t
            LEFT JOIN ResourceTags rt ON rt.TagId = t.Id
            WHERE t.TagSetId = $tagSetId
            GROUP BY t.Id, t.Name
            ORDER BY t.Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);

        var result = new List<TagSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new TagSummary(reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    public async Task SetTagsAsync(string path, bool isDirectory, IEnumerable<string> tags)
    {
        var cleanTags = tags
            .Select(CleanTag)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var pathKey = NormalizePath(path);

        var resource = connection.CreateCommand();
        resource.Transaction = transaction;
        resource.CommandText = """
            INSERT INTO Resources(PathKey, OriginalPath, IsDirectory)
            VALUES($pathKey, $path, $isDirectory)
            ON CONFLICT(PathKey) DO UPDATE SET
                OriginalPath = excluded.OriginalPath,
                IsDirectory = excluded.IsDirectory;
            """;
        resource.Parameters.AddWithValue("$pathKey", pathKey);
        resource.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        resource.Parameters.AddWithValue("$isDirectory", isDirectory ? 1 : 0);
        await resource.ExecuteNonQueryAsync();

        var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = """
            DELETE FROM ResourceTags
            WHERE ResourcePathKey = $pathKey
              AND TagId IN (SELECT Id FROM Tags WHERE TagSetId = $tagSetId);
            """;
        clear.Parameters.AddWithValue("$pathKey", pathKey);
        clear.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
        await clear.ExecuteNonQueryAsync();

        foreach (var tag in cleanTags)
        {
            var normalizedTag = NormalizeTag(tag);
            var addTag = connection.CreateCommand();
            addTag.Transaction = transaction;
            addTag.CommandText = """
                INSERT INTO Tags(Name, NormalizedName, TagSetId) VALUES($name, $normalized, $tagSetId)
                ON CONFLICT(TagSetId, NormalizedName) DO UPDATE SET Name = excluded.Name;
                """;
            addTag.Parameters.AddWithValue("$name", tag);
            addTag.Parameters.AddWithValue("$normalized", normalizedTag);
            addTag.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
            await addTag.ExecuteNonQueryAsync();

            var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = """
                INSERT OR IGNORE INTO ResourceTags(ResourcePathKey, TagId)
                SELECT $pathKey, Id FROM Tags
                WHERE NormalizedName = $normalized AND TagSetId = $tagSetId;
                """;
            link.Parameters.AddWithValue("$pathKey", pathKey);
            link.Parameters.AddWithValue("$normalized", normalizedTag);
            link.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
            await link.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task AddTagsToResourcesAsync(
        IEnumerable<TagResourceTarget> resources,
        IEnumerable<string> tags)
    {
        var targets = resources
            .Select(resource => new TagResourceTarget(Path.GetFullPath(resource.Path), resource.IsDirectory))
            .DistinctBy(resource => NormalizePath(resource.Path), StringComparer.Ordinal)
            .ToList();
        var cleanTags = tags
            .Select(CleanTag)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (targets.Count == 0 || cleanTags.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        foreach (var tag in cleanTags)
        {
            var addTag = connection.CreateCommand();
            addTag.Transaction = transaction;
            addTag.CommandText = """
                INSERT INTO Tags(Name, NormalizedName, TagSetId) VALUES($name, $normalized, $tagSetId)
                ON CONFLICT(TagSetId, NormalizedName) DO UPDATE SET Name = excluded.Name;
                """;
            addTag.Parameters.AddWithValue("$name", tag);
            addTag.Parameters.AddWithValue("$normalized", NormalizeTag(tag));
            addTag.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
            await addTag.ExecuteNonQueryAsync();
        }

        foreach (var target in targets)
        {
            var pathKey = NormalizePath(target.Path);
            var resource = connection.CreateCommand();
            resource.Transaction = transaction;
            resource.CommandText = """
                INSERT INTO Resources(PathKey, OriginalPath, IsDirectory)
                VALUES($pathKey, $path, $isDirectory)
                ON CONFLICT(PathKey) DO UPDATE SET
                    OriginalPath = excluded.OriginalPath,
                    IsDirectory = excluded.IsDirectory;
                """;
            resource.Parameters.AddWithValue("$pathKey", pathKey);
            resource.Parameters.AddWithValue("$path", target.Path);
            resource.Parameters.AddWithValue("$isDirectory", target.IsDirectory ? 1 : 0);
            await resource.ExecuteNonQueryAsync();

            foreach (var tag in cleanTags)
            {
                var link = connection.CreateCommand();
                link.Transaction = transaction;
                link.CommandText = """
                    INSERT OR IGNORE INTO ResourceTags(ResourcePathKey, TagId)
                    SELECT $pathKey, Id FROM Tags
                    WHERE NormalizedName = $normalized AND TagSetId = $tagSetId;
                    """;
                link.Parameters.AddWithValue("$pathKey", pathKey);
                link.Parameters.AddWithValue("$normalized", NormalizeTag(tag));
                link.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
                await link.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    public async Task CreateTagsAsync(IEnumerable<string> tags)
    {
        var cleanTags = tags
            .Select(CleanTag)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (cleanTags.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var tag in cleanTags)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Tags(Name, NormalizedName, TagSetId) VALUES($name, $normalized, $tagSetId)
                ON CONFLICT(TagSetId, NormalizedName) DO UPDATE SET Name = excluded.Name;
                """;
            command.Parameters.AddWithValue("$name", tag);
            command.Parameters.AddWithValue("$normalized", NormalizeTag(tag));
            command.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task DeleteTagAsync(string tagName)
    {
        var normalizedTag = NormalizeTag(tagName);
        if (normalizedTag.Length == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var deleteLinks = connection.CreateCommand();
        deleteLinks.Transaction = transaction;
        deleteLinks.CommandText = """
            DELETE FROM ResourceTags
            WHERE TagId IN (
                SELECT Id FROM Tags WHERE NormalizedName = $normalized AND TagSetId = $tagSetId
            );
            """;
        deleteLinks.Parameters.AddWithValue("$normalized", normalizedTag);
        deleteLinks.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
        await deleteLinks.ExecuteNonQueryAsync();

        var deleteTag = connection.CreateCommand();
        deleteTag.Transaction = transaction;
        deleteTag.CommandText = """
            DELETE FROM Tags WHERE NormalizedName = $normalized AND TagSetId = $tagSetId;
            """;
        deleteTag.Parameters.AddWithValue("$normalized", normalizedTag);
        deleteTag.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
        await deleteTag.ExecuteNonQueryAsync();

        var cleanupResources = connection.CreateCommand();
        cleanupResources.Transaction = transaction;
        cleanupResources.CommandText = """
            DELETE FROM Resources
            WHERE NOT EXISTS (
                SELECT 1 FROM ResourceTags WHERE ResourcePathKey = Resources.PathKey
            );
            """;
        await cleanupResources.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task RemoveTagsFromResourcesAsync(
        IEnumerable<TagResourceTarget> resources,
        IEnumerable<string> tags)
    {
        var pathKeys = resources
            .Select(resource => NormalizePath(resource.Path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var normalizedTags = tags
            .Select(NormalizeTag)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (pathKeys.Count == 0 || normalizedTags.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        foreach (var pathKey in pathKeys)
        foreach (var normalizedTag in normalizedTags)
        {
            var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = """
                DELETE FROM ResourceTags
                WHERE ResourcePathKey = $pathKey
                  AND TagId IN (
                      SELECT Id FROM Tags WHERE NormalizedName = $normalized AND TagSetId = $tagSetId
                  );
                """;
            remove.Parameters.AddWithValue("$pathKey", pathKey);
            remove.Parameters.AddWithValue("$normalized", normalizedTag);
            remove.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
            await remove.ExecuteNonQueryAsync();
        }

        var cleanupResources = connection.CreateCommand();
        cleanupResources.Transaction = transaction;
        cleanupResources.CommandText = """
            DELETE FROM Resources
            WHERE NOT EXISTS (
                SELECT 1 FROM ResourceTags WHERE ResourcePathKey = Resources.PathKey
            );
            """;
        await cleanupResources.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    public async Task RenameTagAsync(string originalName, string newName)
    {
        var originalNormalized = NormalizeTag(originalName);
        var cleanNewName = CleanTag(newName);
        var newNormalized = NormalizeTag(cleanNewName);
        if (originalNormalized.Length == 0 || newNormalized.Length == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var ids = connection.CreateCommand();
        ids.Transaction = transaction;
        ids.CommandText = """
            SELECT
                (SELECT Id FROM Tags WHERE NormalizedName = $original AND TagSetId = $tagSetId),
                (SELECT Id FROM Tags WHERE NormalizedName = $newName AND TagSetId = $tagSetId);
            """;
        ids.Parameters.AddWithValue("$original", originalNormalized);
        ids.Parameters.AddWithValue("$newName", newNormalized);
        ids.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
        long? originalId = null;
        long? existingNewId = null;
        await using (var reader = await ids.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0)) originalId = reader.GetInt64(0);
                if (!reader.IsDBNull(1)) existingNewId = reader.GetInt64(1);
            }
        }
        if (originalId is null)
        {
            await transaction.RollbackAsync();
            return;
        }

        if (existingNewId is not null && existingNewId != originalId)
        {
            var merge = connection.CreateCommand();
            merge.Transaction = transaction;
            merge.CommandText = """
                INSERT OR IGNORE INTO ResourceTags(ResourcePathKey, TagId)
                SELECT ResourcePathKey, $newId FROM ResourceTags WHERE TagId = $oldId;
                DELETE FROM ResourceTags WHERE TagId = $oldId;
                DELETE FROM Tags WHERE Id = $oldId;
                """;
            merge.Parameters.AddWithValue("$newId", existingNewId.Value);
            merge.Parameters.AddWithValue("$oldId", originalId.Value);
            await merge.ExecuteNonQueryAsync();
        }
        else
        {
            var rename = connection.CreateCommand();
            rename.Transaction = transaction;
            rename.CommandText = "UPDATE Tags SET Name = $name, NormalizedName = $normalized WHERE Id = $id;";
            rename.Parameters.AddWithValue("$name", cleanNewName);
            rename.Parameters.AddWithValue("$normalized", newNormalized);
            rename.Parameters.AddWithValue("$id", originalId.Value);
            await rename.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<TaggedPath>> SearchAsync(
        string rootFolder,
        IEnumerable<string> tags,
        TagMatchMode matchMode)
    {
        var normalizedTags = tags
            .Select(CleanTag)
            .Where(tag => tag.Length > 0)
            .Select(NormalizeTag)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedTags.Count == 0) return [];

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        var parameters = normalizedTags.Select((_, index) => $"$tag{index}").ToList();
        var rootKey = NormalizePath(rootFolder);
        var prefix = rootKey.EndsWith(Path.DirectorySeparatorChar) ? rootKey : rootKey + Path.DirectorySeparatorChar;

        command.CommandText = $"""
            SELECT r.OriginalPath, r.IsDirectory, GROUP_CONCAT(t.Name, ', ')
            FROM Resources r
            JOIN ResourceTags rt ON rt.ResourcePathKey = r.PathKey
            JOIN Tags t ON t.Id = rt.TagId
            WHERE (r.PathKey = $root OR substr(r.PathKey, 1, length($prefix)) = $prefix)
              AND t.NormalizedName IN ({string.Join(", ", parameters)})
              AND t.TagSetId = $tagSetId
            GROUP BY r.PathKey, r.OriginalPath, r.IsDirectory
            HAVING COUNT(DISTINCT t.NormalizedName) >= $required
            ORDER BY r.IsDirectory DESC, r.OriginalPath COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$root", rootKey);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$required", matchMode == TagMatchMode.And ? normalizedTags.Count : 1);
        command.Parameters.AddWithValue("$tagSetId", ActiveTagSetId);
        for (var index = 0; index < normalizedTags.Count; index++)
            command.Parameters.AddWithValue(parameters[index], normalizedTags[index]);

        var result = new List<TaggedPath>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new TaggedPath(reader.GetString(0), reader.GetInt64(1) != 0, reader.GetString(2)));
        return result;
    }

    public Task BackupToAsync(string destinationPath) => Task.Run(() =>
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        using var source = new SqliteConnection(_connectionString);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullDestination,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    });

    public async Task<string> CreateSafetyBackupAsync(string reason)
    {
        var safeReason = string.Concat(reason.Where(character => char.IsLetterOrDigit(character) || character == '-'));
        if (safeReason.Length == 0) safeReason = "safety";
        var path = Path.Combine(_backupFolder, $"tags-{safeReason}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        await BackupToAsync(path);
        return path;
    }

    public async Task<string> CreateAutomaticBackupAsync(int retentionCount)
    {
        var path = Path.Combine(_backupFolder, $"tags-auto-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        await BackupToAsync(path);
        var retained = Math.Clamp(retentionCount, 3, 100);
        var backups = Directory.EnumerateFiles(_backupFolder, "tags-auto-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        foreach (var oldBackup in backups.Skip(retained))
        {
            try { File.Delete(oldBackup); }
            catch { }
        }
        return path;
    }

    public async Task RestoreFromAsync(string backupPath)
    {
        await Task.Run(() =>
        {
            var fullPath = Path.GetFullPath(backupPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("태그 백업 파일을 찾을 수 없습니다.", fullPath);

            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            source.Open();
            using (var validation = source.CreateCommand())
            {
                validation.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name IN ('Resources', 'Tags', 'ResourceTags');
                    """;
                if (Convert.ToInt32(validation.ExecuteScalar()) != 3)
                    throw new InvalidDataException("올바른 태그 백업 데이터베이스가 아닙니다.");
            }

            using var destination = new SqliteConnection(_connectionString);
            destination.Open();
            source.BackupDatabase(destination);
        });
        await InitializeAsync();
    }

    public async Task ResetAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ResourceTags; DELETE FROM Resources; DELETE FROM Tags;";
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task ExportAsync(string destinationPath)
    {
        var records = await ReadExportRecordsAsync();
        if (string.Equals(Path.GetExtension(destinationPath), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = new StringBuilder("TagSet,Path,IsDirectory,Tags\r\n");
            foreach (var record in records)
                csv.Append(Csv(record.TagSet)).Append(',')
                    .Append(Csv(record.Path)).Append(',')
                    .Append(record.IsDirectory ? "true" : "false").Append(',')
                    .Append(Csv(string.Join(", ", record.Tags))).Append("\r\n");
            await File.WriteAllTextAsync(destinationPath, csv.ToString(), new UTF8Encoding(true));
        }
        else
        {
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(destinationPath, json, new UTF8Encoding(true));
        }
    }

    public async Task<int> RemapPathsAsync(string oldRoot, string newRoot)
    {
        var oldFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot));
        var newFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot));
        var oldKey = NormalizePath(oldFull);
        var prefix = oldKey + Path.DirectorySeparatorChar;
        var resources = new List<(string OldKey, string OriginalPath, bool IsDirectory)>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var select = connection.CreateCommand();
        select.CommandText = """
            SELECT PathKey, OriginalPath, IsDirectory FROM Resources
            WHERE PathKey = $root OR substr(PathKey, 1, length($prefix)) = $prefix;
            """;
        select.Parameters.AddWithValue("$root", oldKey);
        select.Parameters.AddWithValue("$prefix", prefix);
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                resources.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0));

        await using var transaction = connection.BeginTransaction();
        foreach (var resource in resources)
        {
            var relative = Path.GetRelativePath(oldFull, resource.OriginalPath);
            var newPath = Path.GetFullPath(Path.Combine(newFull, relative));
            var newKey = NormalizePath(newPath);

            var addResource = connection.CreateCommand();
            addResource.Transaction = transaction;
            addResource.CommandText = """
                INSERT INTO Resources(PathKey, OriginalPath, IsDirectory)
                VALUES($newKey, $newPath, $isDirectory)
                ON CONFLICT(PathKey) DO UPDATE SET
                    OriginalPath = excluded.OriginalPath,
                    IsDirectory = excluded.IsDirectory;
                INSERT OR IGNORE INTO ResourceTags(ResourcePathKey, TagId)
                    SELECT $newKey, TagId FROM ResourceTags WHERE ResourcePathKey = $oldKey;
                DELETE FROM ResourceTags WHERE ResourcePathKey = $oldKey;
                DELETE FROM Resources WHERE PathKey = $oldKey;
                """;
            addResource.Parameters.AddWithValue("$newKey", newKey);
            addResource.Parameters.AddWithValue("$newPath", newPath);
            addResource.Parameters.AddWithValue("$isDirectory", resource.IsDirectory ? 1 : 0);
            addResource.Parameters.AddWithValue("$oldKey", resource.OldKey);
            await addResource.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return resources.Count;
    }

    public async Task<int> CopyPathsAsync(string oldRoot, string newRoot)
    {
        var oldFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot));
        var newFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot));
        var oldKey = NormalizePath(oldFull);
        var prefix = oldKey + Path.DirectorySeparatorChar;
        var resources = new List<(string OldKey, string OriginalPath, bool IsDirectory)>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var select = connection.CreateCommand();
        select.CommandText = """
            SELECT PathKey, OriginalPath, IsDirectory FROM Resources
            WHERE PathKey = $root OR substr(PathKey, 1, length($prefix)) = $prefix;
            """;
        select.Parameters.AddWithValue("$root", oldKey);
        select.Parameters.AddWithValue("$prefix", prefix);
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                resources.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0));

        await using var transaction = connection.BeginTransaction();
        foreach (var resource in resources)
        {
            var relative = Path.GetRelativePath(oldFull, resource.OriginalPath);
            var newPath = Path.GetFullPath(Path.Combine(newFull, relative));
            var newKey = NormalizePath(newPath);

            var copyResource = connection.CreateCommand();
            copyResource.Transaction = transaction;
            copyResource.CommandText = """
                INSERT INTO Resources(PathKey, OriginalPath, IsDirectory)
                VALUES($newKey, $newPath, $isDirectory)
                ON CONFLICT(PathKey) DO UPDATE SET
                    OriginalPath = excluded.OriginalPath,
                    IsDirectory = excluded.IsDirectory;
                INSERT OR IGNORE INTO ResourceTags(ResourcePathKey, TagId)
                    SELECT $newKey, TagId FROM ResourceTags WHERE ResourcePathKey = $oldKey;
                """;
            copyResource.Parameters.AddWithValue("$newKey", newKey);
            copyResource.Parameters.AddWithValue("$newPath", newPath);
            copyResource.Parameters.AddWithValue("$isDirectory", resource.IsDirectory ? 1 : 0);
            copyResource.Parameters.AddWithValue("$oldKey", resource.OldKey);
            await copyResource.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return resources.Count;
    }

    private async Task<List<TagExportRecord>> ReadExportRecordsAsync()
    {
        var records = new Dictionary<string, TagExportRecord>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts.Name, r.OriginalPath, r.IsDirectory, t.Name
            FROM Resources r
            JOIN ResourceTags rt ON rt.ResourcePathKey = r.PathKey
            JOIN Tags t ON t.Id = rt.TagId
            JOIN TagSets ts ON ts.Id = t.TagSetId
            ORDER BY ts.Name COLLATE NOCASE, r.OriginalPath COLLATE NOCASE, t.Name COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tagSet = reader.GetString(0);
            var path = reader.GetString(1);
            var key = $"{tagSet}\0{path}";
            if (!records.TryGetValue(key, out var record))
            {
                record = new TagExportRecord(tagSet, path, reader.GetInt64(2) != 0, []);
                records[key] = record;
            }
            record.Tags.Add(reader.GetString(3));
        }
        return records.Values.ToList();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    public static IReadOnlyList<string> ParseTags(string text) => text
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(CleanTag)
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static string CleanTag(string tag) => tag.Trim().TrimStart('#').Trim();
    private static string NormalizeTag(string tag) => CleanTag(tag).ToUpperInvariant();
    private static string CleanTagSetName(string name) => name.Trim();
    private static string NormalizeTagSetName(string name) => CleanTagSetName(name).ToUpperInvariant();
    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
}

public enum TagMatchMode { And, Or }
public sealed record TaggedPath(string Path, bool IsDirectory, string TagsText);
public sealed record TagSummary(string Name, int UsageCount);
public sealed record TagSetSummary(long Id, string Name, int TagCount)
{
    public string DisplayText => $"{Name} ({TagCount:N0})";
}
public sealed record TagExportRecord(string TagSet, string Path, bool IsDirectory, List<string> Tags);
public sealed record TagResourceTarget(string Path, bool IsDirectory);
