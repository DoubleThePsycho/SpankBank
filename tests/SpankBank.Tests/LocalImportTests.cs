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
    }

    private IReadOnlyDictionary<long, ImportCheck> Check(bool skipInferior, params long[] ids) => new ImportChecker(_db.Paths).Check(ids, skipInferior);

    [Fact]
    public void Only_posts_the_site_deleted_or_that_are_lost_and_not_here_are_wanted()
    {
        AddPost(1, new string('a', 32), lost: true);               // deleted + lost
        AddPost(2, new string('b', 32), lost: false);              // still online
        AddPost(3, new string('c', 32), lost: true);
        _store.MarkSaved(_store.GetCatalogPost(3)!, LocalPostStatus.Downloaded); // here already
        _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, IsDeleted) VALUES (4, '2026-01-01', '2026-01-01', '{new string('4', 32)}', 'png', 1);"); // deleted, nobody's watch
        _db.Execute(_db.Paths.AppDatabasePath, "INSERT INTO DeletedPosts (PostId, DeletedAt, State, Reason) VALUES (4, '2026-01-01', 1, 'user');");
        _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, IsDeleted) VALUES (5, '2026-01-01', '2026-01-01', '{new string('5', 32)}', 'png', 1);");

        var verdicts = Check(false, 1, 2, 3, 4, 5, 99);

        Assert.Equal(ImportVerdict.Wanted, verdicts[1].Verdict);
        Assert.Equal(ImportVerdict.StillOnline, verdicts[2].Verdict);
        Assert.Equal(ImportVerdict.AlreadyLocal, verdicts[3].Verdict);
        Assert.Equal(ImportVerdict.Binned, verdicts[4].Verdict);
        Assert.Equal(ImportVerdict.Wanted, verdicts[5].Verdict); // deleted posts are wanted even when no artist watches them
        Assert.Equal(ImportVerdict.NotInCatalog, verdicts[99].Verdict);
    }

    [Fact]
    public void Posts_the_site_still_has_say_whether_a_watch_matches_them_and_the_blacklist_applies_to_any_post()
    {
        AddPost(1, new string('a', 32), lost: false); // matched by the enabled watch, not deleted
        _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, IsDeleted, TagString) VALUES (2, '2026-01-01', '2026-01-01', '{new string('b', 32)}', 'png', 1, 'plain'), (3, '2026-01-01', '2026-01-01', '{new string('c', 32)}', 'png', 0, 'plain'), (4, '2026-01-01', '2026-01-01', '{new string('d', 32)}', 'png', 1, 'unwanted_tag');");

        new SpankBank.Data.Blacklist.BlacklistRepository(_db.Paths.AppDatabasePath).SetAll(["unwanted_tag"]);
        var verdicts = Check(false, 1, 2, 3, 4);

        Assert.Equal(ImportVerdict.StillOnline, verdicts[1].Verdict);
        Assert.Equal(ImportVerdict.Wanted, verdicts[2].Verdict);
        Assert.Equal(ImportVerdict.NotWatched, verdicts[3].Verdict);     // online and nobody's watch matches it
        Assert.Equal(ImportVerdict.Blacklisted, verdicts[4].Verdict);    // deleted, unmatched, but the blacklist still says no

        // An artist set to ignore the global blacklist exempts the posts they match, even while that artist is disabled.
        _db.Execute(_db.Paths.AppDatabasePath, $"UPDATE WatchedItems SET IgnoreBlacklist = 1, Enabled = 0 WHERE Id = {_watch};");
        _db.Execute(_db.Paths.AppDatabasePath, "INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES (" + _watch + ", 4);");
        Assert.Equal(ImportVerdict.Wanted, Check(false, 4)[4].Verdict);
    }

    [Fact]
    public void Inferior_versions_and_edits_are_refused_only_with_the_setting_on_and_unknown_reasons_are_reported()
    {
        foreach (var id in new[] { 1, 2, 3 })
            _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, IsDeleted) VALUES ({id}, '2026-01-01', '2026-01-01', '{new string((char)('0' + id), 32)}', 'png', 1);");
        _store.SaveDeletionReasons(new Dictionary<long, string?>
        {
            [1] = "Inferior version/duplicate of post #5",
            [2] = "Not wanted here",
        });

        var off = Check(false, 1, 2, 3);
        Assert.All(off.Values, r => Assert.Equal(ImportVerdict.Wanted, r.Verdict)); // setting off: everything deleted is taken

        var on = Check(true, 1, 2, 3);
        Assert.Equal(ImportVerdict.Inferior, on[1].Verdict);
        Assert.Contains("#5", on[1].Message);
        Assert.Equal(ImportVerdict.Wanted, on[2].Verdict);
        Assert.Equal(ImportVerdict.ReasonUnknown, on[3].Verdict);
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
