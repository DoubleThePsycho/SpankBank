using System.Security.Cryptography;
using SpankBank.Core.Models;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class LocalImportTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "spankbank-import-" + Guid.NewGuid().ToString("N"));
    private readonly DownloadStore _store;
    private readonly LocalImportStore _imports;
    private readonly WatchesRepository _watches;
    private readonly long _watch;

    public LocalImportTests()
    {
        Directory.CreateDirectory(_folder);
        _store = new DownloadStore(_db.Paths);
        _imports = new LocalImportStore(_db.Paths, _store);
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        _watch = _watches.Add(WatchedItemType.Artist, "someone", "someone")!.Value;
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    /// <summary>A matched post in the catalog; <paramref name="lost"/> adds the failure the downloader records for a deleted post nobody has.</summary>
    private void AddPost(long id, string md5, bool lost)
    {
        _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, IsDeleted, FileSize) VALUES ({id}, '2026-01-01', '2026-01-01', '{md5}', 'png', {(lost ? 1 : 0)}, 5);");
        _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({_watch}, {id});");
        if (lost)
            _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO DownloadAttempts (PostId, Attempts, LastAttemptAt, NextAttemptAt, LastError) VALUES ({id}, 1, '2026-01-01', '2027-01-01', 'Deleted on e621 and not found in any archive.');");
    }

    private string Stage(byte[] data, string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Theory]
    [InlineData("03469863.jpg", "jpg")]
    [InlineData("[001] 03646440.JPEG", "jpg")]
    [InlineData("03646440.raw.png", "png")]
    [InlineData("clip.webm", "webm")]
    [InlineData("notes.txt", null)]
    [InlineData("noext", null)]
    public void Only_file_types_the_site_hosts_are_accepted(string name, string? expected)
        => Assert.Equal(expected, LocalImportStore.NormalizeExtension(name));

    [Fact]
    public void Lost_and_pending_posts_are_listed_together_and_local_ones_are_left_out()
    {
        AddPost(1, new string('a', 32), lost: true);
        AddPost(2, new string('b', 32), lost: false);
        AddPost(3, new string('c', 32), lost: false);
        _store.MarkSaved(_store.GetCatalogPost(3)!, LocalPostStatus.Downloaded);

        var missing = _watches.GetMissingPosts(_watch, 1, 50).Items;
        Assert.Equal([2L, 1L], missing.Select(p => p.Id).ToArray());
        Assert.Equal([false, true], missing.Select(p => p.IsLost).ToArray());
        Assert.Equal(MissingState.Missing, _watches.CheckMissing(_watch, 1).State);
        Assert.Equal(MissingState.Missing, _watches.CheckMissing(_watch, 2).State);
        Assert.Equal(MissingState.AlreadyLocal, _watches.CheckMissing(_watch, 3).State);
        Assert.Equal(MissingState.NotInCatalog, _watches.CheckMissing(_watch, 99).State);

        _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt) VALUES (50, '2026-01-01', '2026-01-01', '{new string('9', 32)}', 'png');");
        Assert.Equal(MissingState.NotListed, _watches.CheckMissing(_watch, 50).State);
    }

    [Fact]
    public void A_file_with_the_catalog_md5_is_filed_as_the_original()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var md5 = Md5(data);
        AddPost(1, md5, lost: true);
        _imports.Queue(1, Stage(data, "1.png"), md5, "png", data.Length, "00000001.raw.png");

        Assert.Equal(1, _imports.Process(_folder + "/media"));

        Assert.True(File.Exists(MediaPaths.Original(_folder + "/media", md5, "png")));
        Assert.Equal((long)LocalPostStatus.Recovered, _db.Scalar(_db.Paths.DeviceDatabasePath, "SELECT Status FROM Posts WHERE Id = 1;"));
        Assert.Empty(_imports.GetAll());
        Assert.Empty(_watches.GetMissingPosts(_watch, 1, 50).Items);
    }

    [Fact]
    public void Any_other_file_is_kept_as_custom_under_the_catalog_path_with_its_own_extension()
    {
        var catalogMd5 = new string('d', 32);
        var edited = new byte[] { 9, 9, 9 };
        AddPost(1, catalogMd5, lost: true);
        _imports.Queue(1, Stage(edited, "1.jpg"), Md5(edited), "jpg", edited.Length, "00000001.jpg");

        Assert.Equal(1, _imports.Process(_folder + "/media"));

        Assert.True(File.Exists(MediaPaths.Original(_folder + "/media", catalogMd5, "jpg")));
        Assert.Equal((long)LocalPostStatus.Custom, _db.Scalar(_db.Paths.DeviceDatabasePath, "SELECT Status FROM Posts WHERE Id = 1;"));
        Assert.Equal(edited.Length, _db.Scalar(_db.Paths.DeviceDatabasePath, "SELECT FileSize FROM Posts WHERE Id = 1;"));
        Assert.Equal(1, _watches.GetPostStats(WatchedItemType.Artist)[_watch].Restored); // custom files count as restored
    }

    [Fact]
    public void A_staged_file_that_vanished_is_dropped_and_an_unknown_post_is_reported()
    {
        AddPost(1, new string('e', 32), lost: true);
        _imports.Queue(1, Path.Combine(_folder, "gone.png"), new string('f', 32), "png", 1, null);
        _imports.Queue(77, Stage([1], "77.png"), new string('f', 32), "png", 1, null);

        Assert.Equal(0, _imports.Process(_folder + "/media"));

        var left = Assert.Single(_imports.GetAll());
        Assert.Equal(77, left.PostId);
        Assert.NotNull(left.Error);
    }
}
