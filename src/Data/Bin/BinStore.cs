using Microsoft.Data.Sqlite;
using SpankBank.Data.Downloads;

namespace SpankBank.Data.RubbishBin;

/// <summary>States of a row in DeletedPosts.</summary>
public enum BinState
{
    /// <summary>Delete requested; the worker has not moved the files yet.</summary>
    Requested = 0,
    /// <summary>Files are in the bin.</summary>
    Binned = 1,
    /// <summary>Restore requested.</summary>
    RestoreRequested = 2,
    /// <summary>Files gone for good. Kept as a tombstone so the post is never downloaded again.</summary>
    Purged = 3,
    /// <summary>Purge requested (Empty bin).</summary>
    PurgeRequested = 4,
}

public sealed record BinnedPost(long PostId, string Md5, string Ext, DateTimeOffset DeletedAt, BinState State)
{
    public DateTimeOffset PurgeAt => DeletedAt + BinStore.Retention;
}

/// <summary>
/// The rubbish bin. The web asks (rows in DeletedPosts); the worker does the work: moves the files to
/// &lt;media&gt;/.bin/ (same layout), hides the post by clearing SavedAt in device.sqlite, restores, or purges.
/// A post with any DeletedPosts row is never downloaded again.
/// </summary>
public sealed class BinStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const string BinFolder = ".bin";

    private readonly DatabasePaths _paths;

    public BinStore(DatabasePaths paths) => _paths = paths;

    // ---------------------------------------------------------------- the web side

    /// <summary>Asks for a local post to be moved to the bin. False when it is not on this device or already deleted.</summary>
    public bool RequestDelete(long postId)
    {
        using var device = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath}");
        device.Open();
        using (var exists = device.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM Posts WHERE Id = $id AND SavedAt IS NOT NULL;";
            exists.Parameters.AddWithValue("$id", postId);
            if (exists.ExecuteScalar() is null) return false;
        }

        using var app = OpenApp();
        using var cmd = app.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO DeletedPosts (PostId, DeletedAt, State) VALUES ($id, $now, 0);
            """;
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool RequestRestore(long postId) => SetState(postId, BinState.RestoreRequested, from: [BinState.Binned, BinState.Requested]);

    /// <summary>Empty bin: everything binned (or waiting to be binned) is purged.</summary>
    public int RequestEmpty()
    {
        using var app = OpenApp();
        using var cmd = app.CreateCommand();
        cmd.CommandText = "UPDATE DeletedPosts SET State = 4 WHERE State IN (0, 1);";
        return cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<BinnedPost> List()
    {
        using var connection = OpenAppWithDevice();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT x.PostId, COALESCE(d.Md5, ''), COALESCE(d.FileExt, ''), x.DeletedAt, x.State
            FROM DeletedPosts x LEFT JOIN dev.Posts d ON d.Id = x.PostId
            WHERE x.State <> 3 ORDER BY x.DeletedAt DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var posts = new List<BinnedPost>();
        while (reader.Read())
            posts.Add(new BinnedPost(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), (BinState)reader.GetInt32(4)));
        return posts;
    }

    public BinnedPost? Get(long postId) => List().FirstOrDefault(p => p.PostId == postId);

    // ---------------------------------------------------------------- the worker side

    /// <summary>Does everything that is waiting. Safe to run at any time and again after a crash.</summary>
    /// <returns>How many posts changed state.</returns>
    public int Process(string mediaRoot, DateTimeOffset now)
    {
        var changed = 0;
        foreach (var row in Rows())
        {
            var state = row.State;
            if (state == BinState.Binned && now - row.DeletedAt >= Retention) state = BinState.PurgeRequested;

            switch (state)
            {
                case BinState.Requested:
                    MoveFiles(mediaRoot, row.PostId, intoBin: true);
                    Hide(row.PostId, row.SavedAt);
                    SetState(row.PostId, BinState.Binned, from: [BinState.Requested]);
                    changed++;
                    break;

                case BinState.RestoreRequested:
                    MoveFiles(mediaRoot, row.PostId, intoBin: false);
                    Unhide(row.PostId, row.SavedAt);
                    Forget(row.PostId);
                    changed++;
                    break;

                case BinState.PurgeRequested:
                    PurgeFiles(mediaRoot, row.PostId);
                    DropDeviceRow(row.PostId);
                    SetState(row.PostId, BinState.Purged, from: [BinState.PurgeRequested, BinState.Binned]);
                    changed++;
                    break;
            }
        }
        return changed;
    }

    private sealed record Row(long PostId, DateTimeOffset DeletedAt, BinState State, string? SavedAt);

    private List<Row> Rows()
    {
        using var connection = OpenAppWithDevice();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT x.PostId, x.DeletedAt, x.State, COALESCE(x.OriginalSavedAt, d.SavedAt)
            FROM DeletedPosts x LEFT JOIN dev.Posts d ON d.Id = x.PostId
            WHERE x.State <> 3;
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
            rows.Add(new Row(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), (BinState)reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    private (string Md5, string Ext)? PostFiles(long postId)
    {
        using var device = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath}");
        device.Open();
        using var cmd = device.CreateCommand();
        cmd.CommandText = "SELECT Md5, FileExt FROM Posts WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1)) return null;
        var (md5, ext) = (reader.GetString(0), reader.GetString(1));
        return md5.Length == 32 && ext.Length > 0 ? (md5, ext) : null;
    }

    private IEnumerable<string> RelativeFiles(string md5, string ext)
    {
        yield return Path.Combine(md5[..2], md5[2..4], $"{md5}.{ext}");
        yield return Path.Combine("preview", md5[..2], md5[2..4], $"{md5}.jpg");
        yield return Path.Combine("sample", md5[..2], md5[2..4], $"{md5}.jpg");
    }

    private void MoveFiles(string mediaRoot, long postId, bool intoBin)
    {
        if (PostFiles(postId) is not var (md5, ext)) return;
        foreach (var relative in RelativeFiles(md5, ext))
        {
            var live = Path.Combine(mediaRoot, relative);
            var bin = Path.Combine(mediaRoot, BinFolder, relative);
            var (from, to) = intoBin ? (live, bin) : (bin, live);
            if (!File.Exists(from)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
        }
    }

    private void PurgeFiles(string mediaRoot, long postId)
    {
        if (PostFiles(postId) is not var (md5, ext)) return;
        foreach (var relative in RelativeFiles(md5, ext))
        {
            // Both places: a purge of a post still waiting to be binned must not leave the live file behind.
            foreach (var path in new[] { Path.Combine(mediaRoot, BinFolder, relative), Path.Combine(mediaRoot, relative) })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    // device.sqlite: "SavedAt IS NOT NULL" is what makes a post local everywhere, so clearing it hides the post.
    private void Hide(long postId, string? savedAt)
    {
        using var device = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath}");
        device.Open();
        string? original = savedAt;
        using (var read = device.CreateCommand())
        {
            read.CommandText = "SELECT SavedAt FROM Posts WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", postId);
            if (read.ExecuteScalar() is string current) original = current;
        }
        using (var cmd = device.CreateCommand())
        {
            cmd.CommandText = "UPDATE Posts SET SavedAt = NULL WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", postId);
            cmd.ExecuteNonQuery();
        }
        if (original is null) return;
        using var app = OpenApp();
        using var keep = app.CreateCommand();
        keep.CommandText = "UPDATE DeletedPosts SET OriginalSavedAt = COALESCE(OriginalSavedAt, $saved) WHERE PostId = $id;";
        keep.Parameters.AddWithValue("$saved", original);
        keep.Parameters.AddWithValue("$id", postId);
        keep.ExecuteNonQuery();
    }

    private void Unhide(long postId, string? savedAt)
    {
        using var device = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath}");
        device.Open();
        using var cmd = device.CreateCommand();
        cmd.CommandText = "UPDATE Posts SET SavedAt = COALESCE($saved, $now) WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$saved", (object?)savedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.ExecuteNonQuery();
    }

    private void DropDeviceRow(long postId)
    {
        using var device = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath}");
        device.Open();
        using var cmd = device.CreateCommand();
        cmd.CommandText = "DELETE FROM Posts WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.ExecuteNonQuery();
    }

    private void Forget(long postId)
    {
        using var app = OpenApp();
        using var cmd = app.CreateCommand();
        cmd.CommandText = "DELETE FROM DeletedPosts WHERE PostId = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.ExecuteNonQuery();
    }

    private bool SetState(long postId, BinState to, BinState[] from)
    {
        using var app = OpenApp();
        using var cmd = app.CreateCommand();
        cmd.CommandText = $"UPDATE DeletedPosts SET State = $to WHERE PostId = $id AND State IN ({string.Join(", ", from.Select(s => (int)s))});";
        cmd.Parameters.AddWithValue("$to", (int)to);
        cmd.Parameters.AddWithValue("$id", postId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private SqliteConnection OpenApp()
    {
        var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        return connection;
    }

    private SqliteConnection OpenAppWithDevice()
    {
        var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{_paths.DeviceDatabasePath.Replace("'", "''")}' AS dev;";
        cmd.ExecuteNonQuery();
        return connection;
    }
}
