using Microsoft.Data.Sqlite;
using SkiaSharp;
using SpankBank.Core.Models;

namespace SpankBank.Data.Downloads;

/// <summary>One uploaded file waiting for the worker (<c>LocalImports</c>).</summary>
public sealed record LocalImport(long PostId, string StagedPath, string Md5, string Ext, long FileSize, string? FileName, string? Error);

/// <summary>
/// Files the user brings from their own PC for posts that are lost or still pending. The web app stages the upload under
/// <see cref="SpankBankPaths.UploadsDir"/> and queues a row (it never writes the media folder); the worker then files it at the
/// post's catalog path. A file whose md5 is the catalog's is the original; any other is the user's own edited version
/// (<see cref="LocalPostStatus.Custom"/>), stored under the same path so everything that finds files by md5 keeps working.
/// </summary>
public sealed class LocalImportStore
{
    /// <summary>What e621 hosts; the only file types accepted.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jpg", "png", "gif", "webp", "webm", "mp4", "swf" };

    public const string Source = "your own files";

    private readonly DatabasePaths _paths;
    private readonly DownloadStore _store;

    public LocalImportStore(DatabasePaths paths, DownloadStore store)
    {
        _paths = paths;
        _store = store;
    }

    /// <summary>"jpeg" becomes "jpg"; null when the file name has no accepted extension.</summary>
    public static string? NormalizeExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg") ext = "jpg";
        return Extensions.Contains(ext) ? ext : null;
    }

    /// <summary>Whether the file is the catalog's own (same md5) rather than a post-processed copy.</summary>
    public static bool IsOriginal(string fileMd5, string catalogMd5) => string.Equals(fileMd5, catalogMd5, StringComparison.OrdinalIgnoreCase);

    public void Queue(long postId, string stagedPath, string md5, string ext, long size, string? fileName)
    {
        using var connection = OpenApp();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LocalImports (PostId, StagedPath, Md5, Ext, FileSize, FileName, AddedAt, Error)
            VALUES ($id, $path, $md5, $ext, $size, $name, $now, NULL)
            ON CONFLICT(PostId) DO UPDATE SET StagedPath = $path, Md5 = $md5, Ext = $ext, FileSize = $size, FileName = $name, AddedAt = $now, Error = NULL;
            """;
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.Parameters.AddWithValue("$path", stagedPath);
        cmd.Parameters.AddWithValue("$md5", md5);
        cmd.Parameters.AddWithValue("$ext", ext);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$name", (object?)fileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<LocalImport> GetAll()
    {
        using var connection = OpenApp();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PostId, StagedPath, Md5, Ext, FileSize, FileName, Error FROM LocalImports ORDER BY AddedAt;";
        using var reader = cmd.ExecuteReader();
        var rows = new List<LocalImport>();
        while (reader.Read())
            rows.Add(new LocalImport(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return rows;
    }

    /// <summary>Files every queued upload under <paramref name="mediaRoot"/> (worker). Returns how many posts were saved.</summary>
    public int Process(string mediaRoot)
    {
        var saved = 0;
        foreach (var item in GetAll())
        {
            try
            {
                if (!File.Exists(item.StagedPath)) { Remove(item.PostId); continue; }
                var post = _store.GetCatalogPost(item.PostId);
                if (post is null) { Fail(item.PostId, "The post is not in the catalog."); continue; }

                var original = IsOriginal(item.Md5, post.Md5);
                var destination = MediaPaths.Original(mediaRoot, post.Md5, item.Ext);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var part = destination + ".part"; // media may be another disk than the staging folder: copy, then rename into place
                File.Copy(item.StagedPath, part, overwrite: true);
                File.Move(part, destination, overwrite: true);

                var (width, height) = post.Width is null || !original ? ImageSize(destination) ?? (post.Width, post.Height) : (post.Width, post.Height);
                MakePreview(destination, item.Ext, MediaPaths.Preview(mediaRoot, post.Md5));

                var status = !original ? LocalPostStatus.Custom : post.IsDeleted ? LocalPostStatus.Recovered : LocalPostStatus.Downloaded;
                _store.MarkSaved(post with { Ext = item.Ext, FileSize = item.FileSize, Width = width, Height = height },
                    status, status == LocalPostStatus.Downloaded ? null : Source);
                File.Delete(item.StagedPath);
                Remove(item.PostId);
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                Fail(item.PostId, ex.Message);
            }
        }
        return saved;
    }

    private static (int? Width, int? Height)? ImageSize(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception) { return null; }
    }

    private static void MakePreview(string file, string ext, string previewPath)
    {
        if (ext is not ("jpg" or "png" or "gif" or "webp") || File.Exists(previewPath)) return;
        try { FlashPreview.WriteJpeg(file, previewPath); }
        catch (Exception) { /* no preview: the post is still saved */ }
    }

    private void Remove(long postId) => Execute("DELETE FROM LocalImports WHERE PostId = $id;", postId, null);
    private void Fail(long postId, string error) => Execute("UPDATE LocalImports SET Error = $error WHERE PostId = $id;", postId, error);

    private void Execute(string sql, long postId, string? error)
    {
        using var connection = OpenApp();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", postId);
        if (error is not null) cmd.Parameters.AddWithValue("$error", error);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenApp()
    {
        var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        return connection;
    }
}
