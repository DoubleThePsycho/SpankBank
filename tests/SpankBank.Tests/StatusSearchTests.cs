using SpankBank.Core.Search;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class StatusSearchTests : IDisposable
{
    private readonly TempDatabases _db = new();

    public StatusSearchTests()
    {
        // Posts 1 and 2 are on the device and active on the site, 3 is on the device but deleted on the site, 4 is deleted and a custom file.
        for (var id = 1; id <= 4; id++)
        {
            var md5 = new string((char)('a' + id), 32);
            _db.Execute(_db.Paths.DeviceDatabasePath,
                $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, Rating, Score, FileExt, TagString, SavedAt, Status, IsDeleted) VALUES ({id}, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '{md5}', 0, 1, 'png', 'solo', '2026-01-02T00:00:00Z', {(id == 4 ? 4 : 0)}, {(id >= 3 ? 1 : 0)});");
            _db.Execute(_db.Paths.ServerDatabasePath,
                $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, TagString, IsDeleted) VALUES ({id}, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '{md5}', 'png', 'solo', {(id >= 3 ? 1 : 0)});");
        }
    }

    public void Dispose() => _db.Dispose();

    private long[] Ids(string search) => new DownloadedPostSearch(_db.Paths).Search(search, 1, 50).Items.Select(i => i.Id).Order().ToArray();

    [Fact]
    public void Without_a_status_every_local_post_is_listed_and_the_deleted_one_is_marked()
    {
        var page = new DownloadedPostSearch(_db.Paths).Search("", 1, 50);

        Assert.Equal([1L, 2L, 3L, 4L], page.Items.Select(i => i.Id).Order().ToArray());
        Assert.Equal([3L, 4L], page.Items.Where(i => i.IsDeleted).Select(i => i.Id).Order().ToArray());
    }

    [Theory]
    [InlineData("status:deleted", new long[] { 3 })]            // deleted on the site; a custom file is its own kind
    [InlineData("status:custom", new long[] { 4 })]
    [InlineData("status:active", new long[] { 1, 2 })]
    [InlineData("status:any", new long[] { 1, 2, 3, 4 })]
    [InlineData("-status:deleted", new long[] { 1, 2, 4 })]
    [InlineData("-status:active", new long[] { 3, 4 })]
    [InlineData("-status:custom", new long[] { 1, 2, 3 })]
    [InlineData("status:active status:custom", new long[] { 1, 2, 4 })]
    [InlineData("status:deleted status:custom", new long[] { 3, 4 })]
    [InlineData("-status:active -status:custom", new long[] { 3 })]
    public void Status_kinds_add_up_and_a_negated_one_is_taken_away(string search, long[] expected) => Assert.Equal(expected, Ids(search));

    [Fact]
    public void Excluding_every_status_is_refused()
    {
        var query = PostSearchQuery.Parse("-status:any");

        Assert.NotNull(query.Error);
        Assert.NotNull(new DownloadedPostSearch(_db.Paths).Search("-status:any", 1, 50).Error);
    }
}
