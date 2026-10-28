using Microsoft.Data.Sqlite;

namespace SpankBank.Data;

public sealed record DatabasePaths(
    string ServerDatabasePath,
    string DeviceDatabasePath,
    string AppDatabasePath);

public static class DatabaseInitializer
{
    public static DatabasePaths GetDatabasePaths()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpankBank");
        Directory.CreateDirectory(folder);

        return new DatabasePaths(
            ServerDatabasePath: Path.Combine(folder, "server.sqlite"),
            DeviceDatabasePath: Path.Combine(folder, "device.sqlite"),
            AppDatabasePath: Path.Combine(folder, "SpankBank.sqlite"));
    }

    public static DatabasePaths GetDatabasePaths(string folder)
    {
        Directory.CreateDirectory(folder);

        return new DatabasePaths(
            ServerDatabasePath: Path.Combine(folder, "server.sqlite"),
            DeviceDatabasePath: Path.Combine(folder, "device.sqlite"),
            AppDatabasePath: Path.Combine(folder, "SpankBank.sqlite"));
    }

    public static void Initialize(DatabasePaths paths)
    {
        InitializeDatabase(paths.ServerDatabasePath, CreateServerTables);
        InitializeDatabase(paths.DeviceDatabasePath, CreateDeviceTables);
        InitializeDatabase(paths.AppDatabasePath, CreateAppTables);
    }

    private static void InitializeDatabase(string databasePath, Action<SqliteConnection> createTables)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA foreign_keys = ON;");

        createTables(connection);
    }

    private static void CreateServerTables(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Posts (
                Id             INTEGER PRIMARY KEY,
                UploaderId     INTEGER,
                CreatedAt      TEXT    NOT NULL,
                UpdatedAt      TEXT    NOT NULL,
                Md5            TEXT,
                Source         TEXT,
                Rating         INTEGER NOT NULL DEFAULT 0,
                ImageWidth     INTEGER,
                ImageHeight    INTEGER,
                TagString      TEXT,
                LockedTags     TEXT,
                FavCount       INTEGER NOT NULL DEFAULT 0,
                FileExt        TEXT,
                ParentId       INTEGER,
                ChangeSeq      INTEGER,
                ApproverId     INTEGER,
                FileSize       INTEGER,
                CommentCount   INTEGER NOT NULL DEFAULT 0,
                Description    TEXT,
                Duration       REAL,
                IsDeleted      INTEGER NOT NULL DEFAULT 0,
                IsPending      INTEGER NOT NULL DEFAULT 0,
                IsFlagged      INTEGER NOT NULL DEFAULT 0,
                Score          INTEGER NOT NULL DEFAULT 0,
                UpScore        INTEGER NOT NULL DEFAULT 0,
                DownScore      INTEGER NOT NULL DEFAULT 0,
                IsRatingLocked INTEGER NOT NULL DEFAULT 0,
                IsStatusLocked INTEGER NOT NULL DEFAULT 0,
                IsNoteLocked   INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Posts_CreatedAt ON Posts (CreatedAt);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Posts_UpdatedAt ON Posts (UpdatedAt);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Posts_Rating ON Posts (Rating);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Posts_Score ON Posts (Score);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Posts_FileExt ON Posts (FileExt);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Posts_FileSize ON Posts (FileSize);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Posts_Md5 ON Posts (Md5);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Tags (
                Id        INTEGER PRIMARY KEY,
                Name      TEXT    NOT NULL UNIQUE,
                Category  INTEGER NOT NULL DEFAULT 0,
                PostCount INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Tags_Name ON Tags (Name);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Tags_Category ON Tags (Category);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Tags_PostCount ON Tags (PostCount);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Tags_Category_PostCount ON Tags (Category, PostCount);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS TagAliases (
                Id             INTEGER PRIMARY KEY,
                AntecedentName TEXT NOT NULL,
                ConsequentName TEXT NOT NULL,
                CreatedAt      TEXT,
                Status         TEXT NOT NULL DEFAULT ''
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_TagAliases_ConsequentName ON TagAliases (ConsequentName);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_TagAliases_AntecedentName ON TagAliases (AntecedentName);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_TagAliases_Status ON TagAliases (Status);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Pools (
                Id          INTEGER PRIMARY KEY,
                Name        TEXT NOT NULL,
                CreatedAt   TEXT,
                UpdatedAt   TEXT,
                Description TEXT,
                IsActive    INTEGER NOT NULL DEFAULT 1,
                Category    TEXT NOT NULL DEFAULT '',
                PostIds     TEXT NOT NULL DEFAULT '',
                PostCount   INTEGER NOT NULL DEFAULT 0
            );
            """);

        EnsureColumn(connection, "Pools", "PostCount", "INTEGER NOT NULL DEFAULT 0");
        // Which pools each post is in, in pool order. Rebuilt from Pools.PostIds whenever pools are imported.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS PoolPosts (
                PoolId   INTEGER NOT NULL,
                PostId   INTEGER NOT NULL,
                Position INTEGER NOT NULL,
                PRIMARY KEY (PoolId, PostId)
            ) WITHOUT ROWID;
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_PoolPosts_PostId ON PoolPosts (PostId);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Pools_Name ON Pools (Name);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Pools_Category ON Pools (Category);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_Pools_IsActive ON Pools (IsActive);");
    }

    private static void CreateDeviceTables(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Posts (
                Id          INTEGER PRIMARY KEY,
                CreatedAt   TEXT NOT NULL,
                UpdatedAt   TEXT NOT NULL,
                Md5         TEXT,
                Rating      INTEGER NOT NULL DEFAULT 0,
                Score       INTEGER NOT NULL DEFAULT 0,
                ImageWidth  INTEGER,
                ImageHeight INTEGER,
                FileSize    INTEGER,
                FileExt     TEXT,
                TagString   TEXT,
                Description TEXT,
                Duration    REAL,
                ParentId    INTEGER,
                SavedAt     TEXT,
                Status      INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DevicePosts_SavedAt ON Posts (SavedAt);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DevicePosts_Status ON Posts (Status);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DevicePosts_Md5 ON Posts (Md5);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DevicePosts_ParentId ON Posts (ParentId);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Tags (
                Id        INTEGER PRIMARY KEY,
                Name      TEXT    NOT NULL UNIQUE,
                Category  INTEGER NOT NULL DEFAULT 0,
                PostCount INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DeviceTags_Name ON Tags (Name);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DeviceTags_Category ON Tags (Category);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS TagAliases (
                Id             INTEGER PRIMARY KEY,
                AntecedentName TEXT NOT NULL,
                ConsequentName TEXT NOT NULL,
                CreatedAt      TEXT,
                Status         TEXT NOT NULL DEFAULT ''
            );
            """);

        // Future device alias copies should include active aliases only.
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DeviceTagAliases_ConsequentName ON TagAliases (ConsequentName);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Pools (
                Id          INTEGER PRIMARY KEY,
                Name        TEXT    NOT NULL,
                CreatedAt   TEXT,
                UpdatedAt   TEXT,
                Description TEXT,
                IsActive    INTEGER NOT NULL DEFAULT 1,
                Category    TEXT NOT NULL DEFAULT '',
                PostIds     TEXT NOT NULL DEFAULT '',
                Status      INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DevicePools_Name ON Pools (Name);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DevicePools_Category ON Pools (Category);");
    }

    private static void CreateAppTables(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DownloadQueueEntries (
                Id              INTEGER PRIMARY KEY,
                EntryType       INTEGER NOT NULL DEFAULT 0,
                SourceValue     TEXT    NOT NULL,
                DisplayName     TEXT    NOT NULL DEFAULT '',
                Status          INTEGER NOT NULL DEFAULT 0,
                WatchEnabled    INTEGER NOT NULL DEFAULT 0,
                CreatedAt       TEXT    NOT NULL,
                UpdatedAt       TEXT    NOT NULL,
                LastAttemptedAt TEXT,
                ErrorMessage    TEXT,
                AttemptCount    INTEGER NOT NULL DEFAULT 0,
                MatchedPosts    INTEGER NOT NULL DEFAULT 0,
                DownloadedPosts INTEGER NOT NULL DEFAULT 0,
                FailedPosts     INTEGER NOT NULL DEFAULT 0,
                Destination     TEXT
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DownloadQueueEntries_Status ON DownloadQueueEntries (Status);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS WatchedItems (
                Id                INTEGER PRIMARY KEY,
                ItemType          INTEGER NOT NULL DEFAULT 0,
                SourceValue       TEXT    NOT NULL,
                DisplayName       TEXT    NOT NULL DEFAULT '',
                Enabled           INTEGER NOT NULL DEFAULT 1,
                AutoDownload      INTEGER NOT NULL DEFAULT 0,
                LastCheckedAt     TEXT,
                LastResultSummary TEXT,
                ErrorMessage      TEXT
            );
            """);

        EnsureColumn(connection, "WatchedItems", "LocalName", "TEXT");
        EnsureColumn(connection, "WatchedItems", "MatchCount", "INTEGER NOT NULL DEFAULT 0");

        EnsureColumn(connection, "WatchedItems", "Origin", "TEXT");
        // 1 = this watch downloads posts even when the blacklist matches them.
        EnsureColumn(connection, "WatchedItems", "IgnoreBlacklist", "INTEGER NOT NULL DEFAULT 0");

        // Blacklist: one e621-style search per row. BlockedPosts is what they match right now (worker-computed):
        // those posts are never downloaded and are hidden in listings.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Blacklist (
                Id        INTEGER PRIMARY KEY,
                Query     TEXT NOT NULL UNIQUE COLLATE NOCASE,
                CreatedAt TEXT NOT NULL
            );
            """);
        // The rubbish bin (see Data/Bin/BinStore.cs). A post with any row here is never downloaded again.
        // State: 0 delete requested, 1 binned, 2 restore requested, 3 purged (tombstone), 4 purge requested.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DeletedPosts (
                PostId          INTEGER PRIMARY KEY,
                DeletedAt       TEXT    NOT NULL,
                State           INTEGER NOT NULL DEFAULT 0,
                OriginalSavedAt TEXT
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DeletedPosts_State ON DeletedPosts (State);");
        // Why it was deleted: 'user' keeps a tombstone after the purge (never downloaded again); 'artist' (the artist was
        // removed with their posts) leaves no trace after the purge, so adding the artist again downloads them again.
        EnsureColumn(connection, "DeletedPosts", "Reason", "TEXT NOT NULL DEFAULT 'user'");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS BlockedPosts (
                PostId INTEGER PRIMARY KEY
            ) WITHOUT ROWID;
            """);

        // Pools that contain a watched artist's posts: suggested, added (automatically or by hand) or ignored.
        // State: 0 = suggested, 1 = added to the watches, 2 = ignored.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS PoolSuggestions (
                ArtistWatchId INTEGER NOT NULL REFERENCES WatchedItems (Id) ON DELETE CASCADE,
                PoolId        INTEGER NOT NULL,
                ArtistPosts   INTEGER NOT NULL,
                PoolPosts     INTEGER NOT NULL,
                IsRoot        INTEGER NOT NULL DEFAULT 1,
                State         INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (ArtistWatchId, PoolId)
            ) WITHOUT ROWID;
            """);

        // Posts the downloader could not fetch (yet): when to try again, and why it failed last time.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DownloadAttempts (
                PostId        INTEGER PRIMARY KEY,
                Attempts      INTEGER NOT NULL DEFAULT 0,
                LastAttemptAt TEXT    NOT NULL,
                NextAttemptAt TEXT    NOT NULL,
                LastError     TEXT
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DownloadAttempts_NextAttemptAt ON DownloadAttempts (NextAttemptAt);");

        // One row per file obtained, so the daily limit can be counted.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS DownloadLog (
                Id     INTEGER PRIMARY KEY,
                PostId INTEGER NOT NULL,
                At     TEXT    NOT NULL,
                Status INTEGER NOT NULL
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_DownloadLog_At ON DownloadLog (At);");

        // Catalog posts each watch currently matches (written by the worker). Position is the order inside a pool.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS WatchMatches (
                WatchedItemId INTEGER NOT NULL REFERENCES WatchedItems (Id) ON DELETE CASCADE,
                PostId        INTEGER NOT NULL,
                Position      INTEGER,
                PRIMARY KEY (WatchedItemId, PostId)
            ) WITHOUT ROWID;
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_WatchedItems_ItemType ON WatchedItems (ItemType);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_WatchedItems_Enabled ON WatchedItems (Enabled);");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Favorites (
                PostId    INTEGER PRIMARY KEY,
                CreatedAt TEXT NOT NULL
            );
            """);

        // Nested folders for favorites (design ported from e621ng PR 2572): a favorite is in at most one folder; no row means unfiled.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS FavoriteFolders (
                Id        INTEGER PRIMARY KEY,
                ParentId  INTEGER REFERENCES FavoriteFolders (Id),
                Name      TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_FavoriteFolders_Name ON FavoriteFolders (ParentId, lower(Name)) WHERE ParentId IS NOT NULL;");
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS IX_FavoriteFolders_RootName ON FavoriteFolders (lower(Name)) WHERE ParentId IS NULL;");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS FavoriteFolderPosts (
                PostId   INTEGER PRIMARY KEY,
                FolderId INTEGER NOT NULL REFERENCES FavoriteFolders (Id)
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_FavoriteFolderPosts_FolderId ON FavoriteFolderPosts (FolderId);");

        // Aliases and characters that belong to a watched artist. Each can be switched off; IsManual rows were added by hand.
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS WatchedArtistTags (
                Id            INTEGER PRIMARY KEY,
                WatchedItemId INTEGER NOT NULL REFERENCES WatchedItems (Id) ON DELETE CASCADE,
                Tag           TEXT    NOT NULL,
                Kind          INTEGER NOT NULL DEFAULT 0,
                IsManual      INTEGER NOT NULL DEFAULT 0,
                Enabled       INTEGER NOT NULL DEFAULT 1,
                UNIQUE (WatchedItemId, Tag)
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT ''
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS ImportHistory (
                Id            INTEGER PRIMARY KEY,
                ImportType    TEXT    NOT NULL,
                SourcePath    TEXT    NOT NULL,
                StartedAt     TEXT    NOT NULL,
                CompletedAt   TEXT,
                Succeeded     INTEGER NOT NULL DEFAULT 0,
                RowsProcessed INTEGER NOT NULL DEFAULT 0,
                ErrorMessage  TEXT
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS IX_ImportHistory_StartedAt ON ImportHistory (StartedAt);");
    }

    // Adds a column to a table created by an older version of the schema.
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}';";
            if (cmd.ExecuteScalar() is not null) return;
        }

        Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    public static DatabaseDiagnostics GetDiagnostics(string databasePath)
    {
        var exists = File.Exists(databasePath);
        var size = exists ? new FileInfo(databasePath).Length : 0;
        var tables = new List<string>();
        string? error = null;
        var succeeded = false;

        if (exists)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    tables.Add(reader.GetString(0));
                succeeded = true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        return new DatabaseDiagnostics
        {
            Path = databasePath,
            FileExists = exists,
            FileSizeBytes = size,
            InitializationSucceeded = succeeded,
            InitializationError = error,
            Tables = tables
        };
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void DropColumnIfExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var infoCmd = connection.CreateCommand();
        infoCmd.CommandText = $"PRAGMA table_info({tableName});";
        var exists = false;
        using (var reader = infoCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
            return;

        Execute(connection, $"ALTER TABLE {tableName} DROP COLUMN {columnName};");
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (ColumnExists(connection, tableName, columnName))
            return;

        Execute(connection, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var infoCmd = connection.CreateCommand();
        infoCmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = infoCmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
