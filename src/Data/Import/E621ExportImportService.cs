using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Import;

public sealed class E621ExportImportService
{
    private readonly ImportHistoryRepository _historyRepository;
    private readonly string _connectionString;

    public E621ExportImportService(string serverDatabasePath, string appDatabasePath)
    {
        _connectionString = $"Data Source={serverDatabasePath}";
        _historyRepository = new ImportHistoryRepository(appDatabasePath);
    }

    public Task<ImportSmokeTestResult> SmokeTestAsync(
        string importType,
        string sourcePath,
        CancellationToken cancellationToken = default)
        => Task.Run(() => SmokeTest(importType, sourcePath, cancellationToken), cancellationToken);

    public Task<long> CountDataRowsAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            ValidateSourcePath(sourcePath);
            return CountDataLines(sourcePath, cancellationToken);
        }, cancellationToken);

    private ImportSmokeTestResult SmokeTest(
        string importType,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        ImportSmokeTestResult result;

        try
        {
            ValidateSourcePath(sourcePath);
            var rowsProcessed = CountDataLines(sourcePath, cancellationToken);
            result = new ImportSmokeTestResult(
                importType,
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: true,
                rowsProcessed,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            result = new ImportSmokeTestResult(
                importType,
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: false,
                RowsProcessed: 0,
                ErrorMessage: ex.Message);
        }

        _historyRepository.Add(result);
        return result;
    }

    public Task<TagsImportResult> ImportTagsAsync(
        string sourcePath,
        IProgress<TagsImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ImportTags(sourcePath, progress, cancellationToken), cancellationToken);

    public Task<TagAliasesImportResult> ImportTagAliasesAsync(
        string sourcePath,
        IProgress<TagAliasesImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ImportTagAliases(sourcePath, progress, cancellationToken), cancellationToken);

    public Task<PostsImportResult> ImportPostsAsync(
        string sourcePath,
        IProgress<PostsImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ImportPosts(sourcePath, progress, cancellationToken), cancellationToken);

    public Task<PoolsImportResult> ImportPoolsAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ImportPools(sourcePath, cancellationToken), cancellationToken);

    // Pools are small (tens of thousands of rows), so the table is replaced in one transaction.
    private PoolsImportResult ImportPools(string sourcePath, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        long rowsRead = 0;
        long rowsImported = 0;
        ImportSmokeTestResult result;

        try
        {
            ValidateSourcePath(sourcePath);

            using var stream = OpenReadStream(sourcePath);
            using var reader = new StreamReader(stream);
            using var records = ReadCsvRecords(reader).GetEnumerator();
            if (!records.MoveNext())
                throw new InvalidOperationException("Pools export is empty or missing a header row.");

            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < records.Current.Count; i++)
                columns[records.Current[i].Trim()] = i;
            int Column(string name) => columns.TryGetValue(name, out var index)
                ? index
                : throw new InvalidOperationException($"Pools export is missing required column '{name}'.");
            var (idCol, nameCol, createdCol, updatedCol, descCol, activeCol, categoryCol, postIdsCol) = (
                Column("id"), Column("name"), Column("created_at"), Column("updated_at"),
                Column("description"), Column("is_active"), Column("category"), Column("post_ids"));

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM Pools;";
                deleteCmd.ExecuteNonQuery();
            }

            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT OR REPLACE INTO Pools (Id, Name, CreatedAt, UpdatedAt, Description, IsActive, Category, PostIds, PostCount)
                VALUES ($id, $name, $createdAt, $updatedAt, $description, $isActive, $category, $postIds, $postCount);
                """;
            insertCmd.Parameters.Add("$id", SqliteType.Integer);
            insertCmd.Parameters.Add("$name", SqliteType.Text);
            insertCmd.Parameters.Add("$createdAt", SqliteType.Text);
            insertCmd.Parameters.Add("$updatedAt", SqliteType.Text);
            insertCmd.Parameters.Add("$description", SqliteType.Text);
            insertCmd.Parameters.Add("$isActive", SqliteType.Integer);
            insertCmd.Parameters.Add("$category", SqliteType.Text);
            insertCmd.Parameters.Add("$postIds", SqliteType.Text);
            insertCmd.Parameters.Add("$postCount", SqliteType.Integer);
            insertCmd.Prepare();

            while (records.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowsRead++;
                var fields = records.Current;
                if (!long.TryParse(ReadString(fields, idCol), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;

                // post_ids is a Postgres array literal: {1,2,3}. Keep the order, drop the braces.
                var postIds = ReadString(fields, postIdsCol).Trim('{', '}');
                var postCount = postIds.Length == 0 ? 0 : postIds.Count(c => c == ',') + 1;

                insertCmd.Parameters["$id"].Value = id;
                insertCmd.Parameters["$name"].Value = ReadString(fields, nameCol);
                insertCmd.Parameters["$createdAt"].Value = NormalizeDate(ReadString(fields, createdCol));
                insertCmd.Parameters["$updatedAt"].Value = NormalizeDate(ReadString(fields, updatedCol));
                insertCmd.Parameters["$description"].Value = DbString(EmptyToNull(ReadString(fields, descCol)));
                insertCmd.Parameters["$isActive"].Value = ReadBool(fields, activeCol) ? 1 : 0;
                insertCmd.Parameters["$category"].Value = ReadString(fields, categoryCol);
                insertCmd.Parameters["$postIds"].Value = postIds;
                insertCmd.Parameters["$postCount"].Value = postCount;
                insertCmd.ExecuteNonQuery();
                rowsImported++;
            }

            transaction.Commit();
            result = new ImportSmokeTestResult("pools", sourcePath, startedAt, DateTimeOffset.UtcNow,
                Succeeded: true, rowsImported, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            result = new ImportSmokeTestResult("pools", sourcePath, startedAt, DateTimeOffset.UtcNow,
                Succeeded: false, rowsImported, ErrorMessage: ex.Message);
        }

        _historyRepository.Add(result);
        return new PoolsImportResult(result, rowsRead, rowsImported);
    }

    private PostsImportResult ImportPosts(
        string sourcePath,
        IProgress<PostsImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        long rowsRead = 0;
        long rowsImported = 0;
        ImportSmokeTestResult result;

        try
        {
            ValidateSourcePath(sourcePath);

            using var stream = OpenReadStream(sourcePath);
            using var reader = new StreamReader(stream);
            using var records = ReadCsvRecords(reader).GetEnumerator();
            if (!records.MoveNext())
                throw new InvalidOperationException("Posts export is empty or missing a header row.");

            var header = PostsCsvHeader.Parse(records.Current);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var dropCmd = connection.CreateCommand())
            {
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = "DROP TABLE IF EXISTS temp.PostsImport;";
                dropCmd.ExecuteNonQuery();
            }

            using (var createCmd = connection.CreateCommand())
            {
                createCmd.Transaction = transaction;
                createCmd.CommandText = """
                    CREATE TEMP TABLE PostsImport (
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
                    """;
                createCmd.ExecuteNonQuery();
            }

            using var insertPost = connection.CreateCommand();
            insertPost.Transaction = transaction;
            insertPost.CommandText = """
                INSERT OR REPLACE INTO PostsImport (
                    Id, UploaderId, CreatedAt, UpdatedAt, Md5, Source, Rating,
                    ImageWidth, ImageHeight, TagString, LockedTags, FavCount, FileExt,
                    ParentId, ChangeSeq, ApproverId, FileSize, CommentCount, Description,
                    Duration, IsDeleted, IsPending, IsFlagged, Score, UpScore, DownScore,
                    IsRatingLocked, IsStatusLocked, IsNoteLocked
                )
                VALUES (
                    $id, $uploaderId, $createdAt, $updatedAt, $md5, $source, $rating,
                    $imageWidth, $imageHeight, $tagString, $lockedTags, $favCount, $fileExt,
                    $parentId, $changeSeq, $approverId, $fileSize, $commentCount, $description,
                    $duration, $isDeleted, $isPending, $isFlagged, $score, $upScore, $downScore,
                    $isRatingLocked, $isStatusLocked, $isNoteLocked
                );
                """;
            AddPostParameters(insertPost);
            insertPost.Prepare();

            while (records.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var post = header.ReadPost(records.Current);
                if (post is null)
                    continue;

                rowsRead++;
                BindPost(insertPost, post);
                insertPost.ExecuteNonQuery();
                rowsImported++;

                if (rowsRead % 1000 == 0)
                    progress?.Report(new PostsImportProgress(rowsRead, rowsImported, "Importing posts..."));
            }

            using (var replaceCmd = connection.CreateCommand())
            {
                replaceCmd.Transaction = transaction;
                replaceCmd.CommandText = """
                    DELETE FROM Posts;
                    INSERT INTO Posts (
                        Id, UploaderId, CreatedAt, UpdatedAt, Md5, Source, Rating,
                        ImageWidth, ImageHeight, TagString, LockedTags, FavCount, FileExt,
                        ParentId, ChangeSeq, ApproverId, FileSize, CommentCount, Description,
                        Duration, IsDeleted, IsPending, IsFlagged, Score, UpScore, DownScore,
                        IsRatingLocked, IsStatusLocked, IsNoteLocked
                    )
                    SELECT
                        Id, UploaderId, CreatedAt, UpdatedAt, Md5, Source, Rating,
                        ImageWidth, ImageHeight, TagString, LockedTags, FavCount, FileExt,
                        ParentId, ChangeSeq, ApproverId, FileSize, CommentCount, Description,
                        Duration, IsDeleted, IsPending, IsFlagged, Score, UpScore, DownScore,
                        IsRatingLocked, IsStatusLocked, IsNoteLocked
                    FROM PostsImport;
                    DROP TABLE IF EXISTS temp.PostsImport;
                    """;
                replaceCmd.ExecuteNonQuery();
            }

            transaction.Commit();
            progress?.Report(new PostsImportProgress(rowsRead, rowsImported, "Posts import complete."));

            result = new ImportSmokeTestResult(
                "posts",
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: true,
                rowsImported,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            result = new ImportSmokeTestResult(
                "posts",
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: false,
                rowsImported,
                ErrorMessage: ex.Message);
        }

        _historyRepository.Add(result);
        return new PostsImportResult(result, rowsRead, rowsImported);
    }

    private TagsImportResult ImportTags(
        string sourcePath,
        IProgress<TagsImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        long rowsRead = 0;
        long rowsImportedOrUpdated = 0;
        long rowsSkippedZeroPostCount = 0;
        ImportSmokeTestResult result;

        try
        {
            ValidateSourcePath(sourcePath);

            using var stream = OpenReadStream(sourcePath);
            using var reader = new StreamReader(stream);

            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                throw new InvalidOperationException("Tags export is empty or missing a header row.");

            var header = TagsCsvHeader.Parse(ParseCsvLine(headerLine));

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using (var dropStagingCmd = connection.CreateCommand())
            {
                dropStagingCmd.Transaction = transaction;
                dropStagingCmd.CommandText = "DROP TABLE IF EXISTS temp.TagsImport;";
                dropStagingCmd.ExecuteNonQuery();
            }

            using (var stagingCmd = connection.CreateCommand())
            {
                stagingCmd.Transaction = transaction;
                stagingCmd.CommandText = """
                    CREATE TEMP TABLE TagsImport (
                        Id        INTEGER PRIMARY KEY,
                        Name      TEXT NOT NULL UNIQUE,
                        Category  INTEGER NOT NULL DEFAULT 0,
                        PostCount INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                stagingCmd.ExecuteNonQuery();
            }

            using var insertTag = connection.CreateCommand();
            insertTag.Transaction = transaction;
            insertTag.CommandText = """
                INSERT OR REPLACE INTO TagsImport (Id, Name, Category, PostCount)
                VALUES ($id, $name, $category, $postCount);
                """;
            insertTag.Parameters.Add("$id", SqliteType.Integer);
            insertTag.Parameters.Add("$name", SqliteType.Text);
            insertTag.Parameters.Add("$category", SqliteType.Integer);
            insertTag.Parameters.Add("$postCount", SqliteType.Integer);
            insertTag.Prepare();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                rowsRead++;
                var fields = ParseCsvLine(line);
                var tag = header.ReadTag(fields);
                if (tag is null)
                    continue;

                if (tag.PostCount <= 0)
                {
                    rowsSkippedZeroPostCount++;
                    continue;
                }

                insertTag.Parameters["$id"].Value = tag.Id;
                insertTag.Parameters["$name"].Value = tag.Name;
                insertTag.Parameters["$category"].Value = (int)tag.Category;
                insertTag.Parameters["$postCount"].Value = tag.PostCount;
                insertTag.ExecuteNonQuery();

                rowsImportedOrUpdated++;
                if (rowsRead % 1000 == 0)
                    progress?.Report(new TagsImportProgress(rowsRead, rowsImportedOrUpdated, rowsSkippedZeroPostCount, "Importing tags..."));
            }

            using (var replaceCmd = connection.CreateCommand())
            {
                replaceCmd.Transaction = transaction;
                replaceCmd.CommandText = """
                    DELETE FROM Tags;
                    INSERT INTO Tags (Id, Name, Category, PostCount)
                    SELECT Id, Name, Category, PostCount
                    FROM TagsImport;
                    DELETE FROM TagAliases
                    WHERE ConsequentName NOT IN (SELECT Name FROM Tags);
                    DROP TABLE IF EXISTS temp.TagsImport;
                    """;
                replaceCmd.ExecuteNonQuery();
            }

            transaction.Commit();
            progress?.Report(new TagsImportProgress(rowsRead, rowsImportedOrUpdated, rowsSkippedZeroPostCount, "Tags import complete."));

            result = new ImportSmokeTestResult(
                "tags",
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: true,
                rowsImportedOrUpdated,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            result = new ImportSmokeTestResult(
                "tags",
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: false,
                rowsImportedOrUpdated,
                ErrorMessage: ex.Message);
        }

        _historyRepository.Add(result);
        return new TagsImportResult(result, rowsRead, rowsImportedOrUpdated, rowsSkippedZeroPostCount);
    }

    private TagAliasesImportResult ImportTagAliases(
        string sourcePath,
        IProgress<TagAliasesImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        long rowsRead = 0;
        long activeRows = 0;
        long tagsUpdated = 0;
        ImportSmokeTestResult result;

        try
        {
            ValidateSourcePath(sourcePath);

            using var stream = OpenReadStream(sourcePath);
            using var reader = new StreamReader(stream);
            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                throw new InvalidOperationException("Tag aliases export is empty or missing a header row.");

            var header = TagAliasesCsvHeader.Parse(ParseCsvLine(headerLine));
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using (var dropTempCmd = connection.CreateCommand())
            {
                dropTempCmd.Transaction = transaction;
                dropTempCmd.CommandText = "DROP TABLE IF EXISTS temp.TempTagAliases;";
                dropTempCmd.ExecuteNonQuery();
            }

            using (var tempCmd = connection.CreateCommand())
            {
                tempCmd.Transaction = transaction;
                tempCmd.CommandText = """
                    CREATE TEMP TABLE TempTagAliases (
                        Id             INTEGER PRIMARY KEY,
                        ConsequentName TEXT NOT NULL,
                        AntecedentName TEXT NOT NULL,
                        CreatedAt      TEXT,
                        Status         TEXT NOT NULL DEFAULT ''
                    );
                    """;
                tempCmd.ExecuteNonQuery();
            }

            using var insertTempCmd = connection.CreateCommand();
            insertTempCmd.Transaction = transaction;
            insertTempCmd.CommandText = """
                INSERT OR REPLACE INTO TempTagAliases
                    (Id, ConsequentName, AntecedentName, CreatedAt, Status)
                VALUES
                    ($id, $consequentName, $antecedentName, $createdAt, $status);
                """;
            insertTempCmd.Parameters.Add("$id", SqliteType.Integer);
            insertTempCmd.Parameters.Add("$consequentName", SqliteType.Text);
            insertTempCmd.Parameters.Add("$antecedentName", SqliteType.Text);
            insertTempCmd.Parameters.Add("$createdAt", SqliteType.Text);
            insertTempCmd.Parameters.Add("$status", SqliteType.Text);
            insertTempCmd.Prepare();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                rowsRead++;
                var fields = ParseCsvLine(line);
                var alias = header.ReadAlias(fields);
                if (alias is null)
                    continue;

                if (string.Equals(alias.Status, "active", StringComparison.OrdinalIgnoreCase))
                    activeRows++;
                insertTempCmd.Parameters["$id"].Value = alias.Id;
                insertTempCmd.Parameters["$consequentName"].Value = alias.ConsequentName;
                insertTempCmd.Parameters["$antecedentName"].Value = alias.AntecedentName;
                insertTempCmd.Parameters["$createdAt"].Value = (object?)alias.CreatedAt ?? DBNull.Value;
                insertTempCmd.Parameters["$status"].Value = alias.Status;
                insertTempCmd.ExecuteNonQuery();
                if (rowsRead % 1000 == 0)
                    progress?.Report(new TagAliasesImportProgress(rowsRead, activeRows, tagsUpdated, "Reading tag aliases..."));
            }

            using var replaceAliasesCmd = connection.CreateCommand();
            replaceAliasesCmd.Transaction = transaction;
            replaceAliasesCmd.CommandText = """
                DELETE FROM TagAliases;
                INSERT INTO TagAliases (Id, AntecedentName, ConsequentName, CreatedAt, Status)
                SELECT Id, AntecedentName, ConsequentName, CreatedAt, Status
                FROM TempTagAliases;
                """;
            replaceAliasesCmd.ExecuteNonQuery();

            using var countConsequentsCmd = connection.CreateCommand();
            countConsequentsCmd.Transaction = transaction;
            countConsequentsCmd.CommandText = """
                SELECT COUNT(DISTINCT ConsequentName)
                FROM TempTagAliases
                WHERE Status = 'active';
                """;
            tagsUpdated = Convert.ToInt64(countConsequentsCmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var cleanupAliasesCmd = connection.CreateCommand();
            cleanupAliasesCmd.Transaction = transaction;
            cleanupAliasesCmd.CommandText = "DROP TABLE IF EXISTS temp.TempTagAliases;";
            cleanupAliasesCmd.ExecuteNonQuery();

            transaction.Commit();
            progress?.Report(new TagAliasesImportProgress(rowsRead, activeRows, tagsUpdated, "Tag aliases import complete."));

            result = new ImportSmokeTestResult(
                "tag_aliases",
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: true,
                tagsUpdated,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            result = new ImportSmokeTestResult(
                "tag_aliases",
                sourcePath,
                startedAt,
                DateTimeOffset.UtcNow,
                Succeeded: false,
                tagsUpdated,
                ErrorMessage: ex.Message);
        }

        _historyRepository.Add(result);
        return new TagAliasesImportResult(result, rowsRead, activeRows, tagsUpdated);
    }

    private static void ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidOperationException("No export file path was provided.");

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Export file was not found.", sourcePath);

        if (!IsSupportedExportPath(sourcePath))
            throw new InvalidOperationException("Supported export files must end with .csv or .csv.gz.");
    }

    private static bool IsSupportedExportPath(string sourcePath)
        => sourcePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        || sourcePath.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase);

    private static long CountDataLines(string sourcePath, CancellationToken cancellationToken)
    {
        using var stream = OpenReadStream(sourcePath);
        using var reader = new StreamReader(stream);

        long totalRecords = 0;
        foreach (var _ in ReadCsvRecords(reader))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalRecords++;
        }

        // e621 exports include a header row. This smoke test counts data rows only.
        return totalRecords > 0 ? totalRecords - 1 : 0;
    }

    private static Stream OpenReadStream(string sourcePath)
    {
        var fileStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            FileOptions.SequentialScan);

        if (sourcePath.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase))
            return new GZipStream(fileStream, CompressionMode.Decompress);

        return fileStream;
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static IEnumerable<IReadOnlyList<string>> ReadCsvRecords(TextReader reader)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var sawAny = false;

        while (true)
        {
            var next = reader.Read();
            if (next < 0)
                break;

            sawAny = true;
            var ch = (char)next;
            if (ch == '"')
            {
                if (inQuotes && reader.Peek() == '"')
                {
                    current.Append('"');
                    reader.Read();
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else if ((ch == '\n' || ch == '\r') && !inQuotes)
            {
                if (ch == '\r' && reader.Peek() == '\n')
                    reader.Read();

                values.Add(current.ToString());
                current.Clear();
                yield return values;
                values = [];
                sawAny = false;
            }
            else
            {
                current.Append(ch);
            }
        }

        if (sawAny || current.Length > 0 || values.Count > 0)
        {
            values.Add(current.ToString());
            yield return values;
        }
    }

    private sealed record PostCsvRow(
        long Id,
        long? UploaderId,
        string CreatedAt,
        string UpdatedAt,
        string? Md5,
        string? Source,
        int Rating,
        int? ImageWidth,
        int? ImageHeight,
        string? TagString,
        string? LockedTags,
        int FavCount,
        string? FileExt,
        long? ParentId,
        long? ChangeSeq,
        long? ApproverId,
        long? FileSize,
        int CommentCount,
        string? Description,
        double? Duration,
        bool IsDeleted,
        bool IsPending,
        bool IsFlagged,
        int Score,
        int UpScore,
        int DownScore,
        bool IsRatingLocked,
        bool IsStatusLocked,
        bool IsNoteLocked);

    private sealed record PostsCsvHeader(
        int IdIndex,
        int UploaderIdIndex,
        int CreatedAtIndex,
        int Md5Index,
        int SourceIndex,
        int RatingIndex,
        int ImageWidthIndex,
        int ImageHeightIndex,
        int TagStringIndex,
        int LockedTagsIndex,
        int FavCountIndex,
        int FileExtIndex,
        int ParentIdIndex,
        int ChangeSeqIndex,
        int ApproverIdIndex,
        int FileSizeIndex,
        int CommentCountIndex,
        int DescriptionIndex,
        int DurationIndex,
        int UpdatedAtIndex,
        int IsDeletedIndex,
        int IsPendingIndex,
        int IsFlaggedIndex,
        int ScoreIndex,
        int UpScoreIndex,
        int DownScoreIndex,
        int IsRatingLockedIndex,
        int IsStatusLockedIndex,
        int IsNoteLockedIndex)
    {
        public static PostsCsvHeader Parse(IReadOnlyList<string> columns)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
                index[columns[i].Trim()] = i;

            return new PostsCsvHeader(
                Required(index, "id"),
                Required(index, "uploader_id"),
                Required(index, "created_at"),
                Required(index, "md5"),
                Required(index, "source"),
                Required(index, "rating"),
                Required(index, "image_width"),
                Required(index, "image_height"),
                Required(index, "tag_string"),
                Required(index, "locked_tags"),
                Required(index, "fav_count"),
                Required(index, "file_ext"),
                Required(index, "parent_id"),
                Required(index, "change_seq"),
                Required(index, "approver_id"),
                Required(index, "file_size"),
                Required(index, "comment_count"),
                Required(index, "description"),
                Required(index, "duration"),
                Required(index, "updated_at"),
                Required(index, "is_deleted"),
                Required(index, "is_pending"),
                Required(index, "is_flagged"),
                Required(index, "score"),
                Required(index, "up_score"),
                Required(index, "down_score"),
                Required(index, "is_rating_locked"),
                Required(index, "is_status_locked"),
                Required(index, "is_note_locked"));
        }

        public PostCsvRow? ReadPost(IReadOnlyList<string> fields)
        {
            if (!TryGet(fields, IdIndex, out var idText)
                || !long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
            {
                return null;
            }

            return new PostCsvRow(
                id,
                ReadNullableLong(fields, UploaderIdIndex),
                NormalizeDate(ReadString(fields, CreatedAtIndex)),
                NormalizeDate(ReadString(fields, UpdatedAtIndex)),
                EmptyToNull(ReadString(fields, Md5Index)),
                EmptyToNull(ReadString(fields, SourceIndex)),
                ReadRating(ReadString(fields, RatingIndex)),
                ReadNullableInt(fields, ImageWidthIndex),
                ReadNullableInt(fields, ImageHeightIndex),
                EmptyToNull(ReadString(fields, TagStringIndex)),
                EmptyToNull(ReadString(fields, LockedTagsIndex)),
                ReadInt(fields, FavCountIndex),
                EmptyToNull(ReadString(fields, FileExtIndex))?.ToLowerInvariant(),
                ReadNullableLong(fields, ParentIdIndex),
                ReadNullableLong(fields, ChangeSeqIndex),
                ReadNullableLong(fields, ApproverIdIndex),
                ReadNullableLong(fields, FileSizeIndex),
                ReadInt(fields, CommentCountIndex),
                EmptyToNull(ReadString(fields, DescriptionIndex)),
                ReadNullableDouble(fields, DurationIndex),
                ReadBool(fields, IsDeletedIndex),
                ReadBool(fields, IsPendingIndex),
                ReadBool(fields, IsFlaggedIndex),
                ReadInt(fields, ScoreIndex),
                ReadInt(fields, UpScoreIndex),
                ReadInt(fields, DownScoreIndex),
                ReadBool(fields, IsRatingLockedIndex),
                ReadBool(fields, IsStatusLockedIndex),
                ReadBool(fields, IsNoteLockedIndex));
        }

        private static int Required(Dictionary<string, int> index, string name)
        {
            if (!index.TryGetValue(name, out var value))
                throw new InvalidOperationException($"Posts export is missing required column '{name}'.");

            return value;
        }
    }

    private static void AddPostParameters(SqliteCommand cmd)
    {
        foreach (var name in new[]
        {
            "$id", "$uploaderId", "$createdAt", "$updatedAt", "$md5", "$source", "$rating",
            "$imageWidth", "$imageHeight", "$tagString", "$lockedTags", "$favCount", "$fileExt",
            "$parentId", "$changeSeq", "$approverId", "$fileSize", "$commentCount", "$description",
            "$duration", "$isDeleted", "$isPending", "$isFlagged", "$score", "$upScore", "$downScore",
            "$isRatingLocked", "$isStatusLocked", "$isNoteLocked"
        })
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            cmd.Parameters.Add(parameter);
        }
    }

    private static void BindPost(SqliteCommand cmd, PostCsvRow post)
    {
        cmd.Parameters["$id"].Value = post.Id;
        cmd.Parameters["$uploaderId"].Value = DbNullable(post.UploaderId);
        cmd.Parameters["$createdAt"].Value = post.CreatedAt;
        cmd.Parameters["$updatedAt"].Value = post.UpdatedAt;
        cmd.Parameters["$md5"].Value = DbString(post.Md5);
        cmd.Parameters["$source"].Value = DbString(post.Source);
        cmd.Parameters["$rating"].Value = post.Rating;
        cmd.Parameters["$imageWidth"].Value = DbNullable(post.ImageWidth);
        cmd.Parameters["$imageHeight"].Value = DbNullable(post.ImageHeight);
        cmd.Parameters["$tagString"].Value = DbString(post.TagString);
        cmd.Parameters["$lockedTags"].Value = DbString(post.LockedTags);
        cmd.Parameters["$favCount"].Value = post.FavCount;
        cmd.Parameters["$fileExt"].Value = DbString(post.FileExt);
        cmd.Parameters["$parentId"].Value = DbNullable(post.ParentId);
        cmd.Parameters["$changeSeq"].Value = DbNullable(post.ChangeSeq);
        cmd.Parameters["$approverId"].Value = DbNullable(post.ApproverId);
        cmd.Parameters["$fileSize"].Value = DbNullable(post.FileSize);
        cmd.Parameters["$commentCount"].Value = post.CommentCount;
        cmd.Parameters["$description"].Value = DbString(post.Description);
        cmd.Parameters["$duration"].Value = DbNullable(post.Duration);
        cmd.Parameters["$isDeleted"].Value = post.IsDeleted ? 1 : 0;
        cmd.Parameters["$isPending"].Value = post.IsPending ? 1 : 0;
        cmd.Parameters["$isFlagged"].Value = post.IsFlagged ? 1 : 0;
        cmd.Parameters["$score"].Value = post.Score;
        cmd.Parameters["$upScore"].Value = post.UpScore;
        cmd.Parameters["$downScore"].Value = post.DownScore;
        cmd.Parameters["$isRatingLocked"].Value = post.IsRatingLocked ? 1 : 0;
        cmd.Parameters["$isStatusLocked"].Value = post.IsStatusLocked ? 1 : 0;
        cmd.Parameters["$isNoteLocked"].Value = post.IsNoteLocked ? 1 : 0;
    }

    private static object DbString(string? value)
        => value is null ? DBNull.Value : value;

    private static object DbNullable<T>(T? value)
        where T : struct
        => value.HasValue ? value.Value : DBNull.Value;

    private static string ReadString(IReadOnlyList<string> fields, int index)
        => TryGet(fields, index, out var value) ? value.Trim() : string.Empty;

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ReadInt(IReadOnlyList<string> fields, int index)
        => int.TryParse(ReadString(fields, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static int? ReadNullableInt(IReadOnlyList<string> fields, int index)
        => int.TryParse(ReadString(fields, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? ReadNullableLong(IReadOnlyList<string> fields, int index)
        => long.TryParse(ReadString(fields, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? ReadNullableDouble(IReadOnlyList<string> fields, int index)
        => double.TryParse(ReadString(fields, index), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool ReadBool(IReadOnlyList<string> fields, int index)
    {
        var value = ReadString(fields, index);
        return value == "t"
            || value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadRating(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "q" or "questionable" => (int)PostRating.Questionable,
            "e" or "explicit" => (int)PostRating.Explicit,
            _ => (int)PostRating.Safe,
        };

    private static string NormalizeDate(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToUniversalTime().ToString("o");

        return string.IsNullOrWhiteSpace(value)
            ? DateTimeOffset.UnixEpoch.ToString("o")
            : value.Trim();
    }

    private static bool TryGet(IReadOnlyList<string> fields, int index, out string value)
    {
        if (index >= 0 && index < fields.Count)
        {
            value = fields[index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private sealed record TagsCsvHeader(
        int IdIndex,
        int NameIndex,
        int CategoryIndex,
        int PostCountIndex)
    {
        public static TagsCsvHeader Parse(IReadOnlyList<string> columns)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
                index[columns[i].Trim()] = i;

            return new TagsCsvHeader(
                Required(index, "id"),
                Required(index, "name"),
                Required(index, "category"),
                Required(index, "post_count"));
        }

        public Tag? ReadTag(IReadOnlyList<string> fields)
        {
            if (!TryGet(fields, IdIndex, out var idText)
                || !long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
            {
                return null;
            }

            if (!TryGet(fields, NameIndex, out var name) || string.IsNullOrWhiteSpace(name))
                return null;

            if (!TryGet(fields, CategoryIndex, out var categoryText)
                || !int.TryParse(categoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var categoryValue))
            {
                categoryValue = (int)TagCategory.General;
            }

            if (!TryGet(fields, PostCountIndex, out var postCountText)
                || !int.TryParse(postCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var postCount))
            {
                postCount = 0;
            }

            var category = Enum.IsDefined(typeof(TagCategory), categoryValue)
                ? (TagCategory)categoryValue
                : TagCategory.General;

            return new Tag
            {
                Id = id,
                Name = name.Trim(),
                Category = category,
                PostCount = Math.Max(0, postCount),
            };
        }

        private static int Required(Dictionary<string, int> index, string name)
        {
            if (!index.TryGetValue(name, out var value))
                throw new InvalidOperationException($"Tags export is missing required column '{name}'.");

            return value;
        }

        private static bool TryGet(IReadOnlyList<string> fields, int index, out string value)
        {
            if (index >= 0 && index < fields.Count)
            {
                value = fields[index];
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    private sealed record TagAliasRow(long Id, string AntecedentName, string ConsequentName, string? CreatedAt, string Status);

    private sealed record TagAliasesCsvHeader(
        int AntecedentNameIndex,
        int ConsequentNameIndex,
        int IdIndex,
        int CreatedAtIndex,
        int StatusIndex)
    {
        public static TagAliasesCsvHeader Parse(IReadOnlyList<string> columns)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
                index[columns[i].Trim()] = i;

            return new TagAliasesCsvHeader(
                Required(index, "antecedent_name"),
                Required(index, "consequent_name"),
                Required(index, "id"),
                Required(index, "created_at"),
                Required(index, "status"));
        }

        public TagAliasRow? ReadAlias(IReadOnlyList<string> fields)
        {
            if (!TryGet(fields, IdIndex, out var idText)
                || !long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !TryGet(fields, AntecedentNameIndex, out var antecedentName)
                || !TryGet(fields, ConsequentNameIndex, out var consequentName)
                || !TryGet(fields, CreatedAtIndex, out var createdAt)
                || !TryGet(fields, StatusIndex, out var status))
            {
                return null;
            }

            antecedentName = antecedentName.Trim();
            consequentName = consequentName.Trim();
            if (string.IsNullOrWhiteSpace(antecedentName)
                || string.IsNullOrWhiteSpace(consequentName)
                || string.Equals(antecedentName, consequentName, StringComparison.Ordinal))
            {
                return null;
            }

            return new TagAliasRow(id, antecedentName, consequentName, string.IsNullOrWhiteSpace(createdAt) ? null : createdAt.Trim(), status.Trim());
        }

        private static int Required(Dictionary<string, int> index, string name)
        {
            if (!index.TryGetValue(name, out var value))
                throw new InvalidOperationException($"Tag aliases export is missing required column '{name}'.");

            return value;
        }

        private static bool TryGet(IReadOnlyList<string> fields, int index, out string value)
        {
            if (index >= 0 && index < fields.Count)
            {
                value = fields[index];
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    private sealed class TagsCsvUpsert : IDisposable
    {
        private readonly SqliteCommand _selectById;
        private readonly SqliteCommand _selectByName;
        private readonly SqliteCommand _updateById;
        private readonly SqliteCommand _updateByName;
        private readonly SqliteCommand _insert;
        private readonly SqliteCommand _deleteById;

        public TagsCsvUpsert(SqliteConnection connection, SqliteTransaction transaction)
        {
            _selectById = CreateCommand(connection, transaction, "SELECT Id FROM Tags WHERE Id = $id LIMIT 1;");
            AddParameter(_selectById, "$id");
            _selectById.Prepare();

            _selectByName = CreateCommand(connection, transaction, "SELECT Id FROM Tags WHERE Name = $name LIMIT 1;");
            AddParameter(_selectByName, "$name");
            _selectByName.Prepare();

            _updateById = CreateCommand(connection, transaction, """
                UPDATE Tags
                SET Name = $name,
                    Category = $category,
                    PostCount = $postCount
                WHERE Id = $id;
                """);
            AddTagParameters(_updateById);
            _updateById.Prepare();

            _updateByName = CreateCommand(connection, transaction, """
                UPDATE Tags
                SET Id = $id,
                    Category = $category,
                    PostCount = $postCount
                WHERE Name = $name;
                """);
            AddTagParameters(_updateByName);
            _updateByName.Prepare();

            _insert = CreateCommand(connection, transaction, """
                INSERT INTO Tags
                    (Id, Name, Category, PostCount)
                VALUES
                    ($id, $name, $category, $postCount);
                """);
            AddTagParameters(_insert);
            _insert.Prepare();

            _deleteById = CreateCommand(connection, transaction, "DELETE FROM Tags WHERE Id = $id;");
            AddParameter(_deleteById, "$id");
            _deleteById.Prepare();
        }

        public void Upsert(Tag tag)
        {
            var idMatch = FindById(tag.Id);
            var nameMatch = FindByName(tag.Name);

            if (idMatch.HasValue)
            {
                // Dev/sample or interrupted imports can leave the same tag name on another row.
                // Tags has no local-only fields, so remove the duplicate name row before applying CSV truth.
                if (nameMatch.HasValue && nameMatch.Value != tag.Id)
                    DeleteById(nameMatch.Value);

                ExecuteTagCommand(_updateById, tag);
                return;
            }

            if (nameMatch.HasValue)
            {
                ExecuteTagCommand(_updateByName, tag);
                return;
            }

            ExecuteTagCommand(_insert, tag);
        }

        public void Dispose()
        {
            _selectById.Dispose();
            _selectByName.Dispose();
            _updateById.Dispose();
            _updateByName.Dispose();
            _insert.Dispose();
            _deleteById.Dispose();
        }

        private long? FindById(long id)
        {
            _selectById.Parameters["$id"].Value = id;
            var result = _selectById.ExecuteScalar();
            return result is null || result == DBNull.Value
                ? null
                : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        private long? FindByName(string name)
        {
            _selectByName.Parameters["$name"].Value = name;
            var result = _selectByName.ExecuteScalar();
            return result is null || result == DBNull.Value
                ? null
                : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        private void DeleteById(long id)
        {
            _deleteById.Parameters["$id"].Value = id;
            _deleteById.ExecuteNonQuery();
        }

        private static SqliteCommand CreateCommand(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string commandText)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = commandText;
            return cmd;
        }

        private static void AddTagParameters(SqliteCommand cmd)
        {
            AddParameter(cmd, "$id");
            AddParameter(cmd, "$name");
            AddParameter(cmd, "$category");
            AddParameter(cmd, "$postCount");
        }

        private static void AddParameter(SqliteCommand cmd, string name)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            cmd.Parameters.Add(parameter);
        }

        private static void ExecuteTagCommand(SqliteCommand cmd, Tag tag)
        {
            cmd.Parameters["$id"].Value = tag.Id;
            cmd.Parameters["$name"].Value = tag.Name;
            cmd.Parameters["$category"].Value = (int)tag.Category;
            cmd.Parameters["$postCount"].Value = tag.PostCount;
            cmd.ExecuteNonQuery();
        }
    }
}

public sealed record TagsImportProgress(
    long RowsRead,
    long RowsImportedOrUpdated,
    long RowsSkippedZeroPostCount,
    string Message);
