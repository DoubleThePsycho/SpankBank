using SpankBank.Data.Import;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class PostsImportTests : IDisposable
{
    private const string Header = "id,uploader_id,created_at,md5,source,rating,image_width,image_height,tag_string,locked_tags,fav_count,"
        + "file_ext,parent_id,change_seq,approver_id,file_size,comment_count,description,duration,updated_at,is_deleted,is_pending,"
        + "is_flagged,score,up_score,down_score,is_rating_locked,is_status_locked,is_note_locked";

    private readonly TempDatabases _db = new();
    private readonly string _csvFolder = Path.Combine(Path.GetTempPath(), "spankbank-csv-" + Guid.NewGuid().ToString("N"));
    private readonly E621ExportImportService _importer;
    private readonly SettingsRepository _settings;

    public PostsImportTests()
    {
        Directory.CreateDirectory(_csvFolder);
        _importer = new E621ExportImportService(_db.Paths.ServerDatabasePath, _db.Paths.AppDatabasePath);
        _settings = new SettingsRepository(_db.Paths.AppDatabasePath);
        for (var i = 1; i <= 1000; i++)
            _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt) VALUES ({i}, '2020-01-01', '2020-01-01');");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_csvFolder, recursive: true); } catch (IOException) { }
    }

    private string Csv(string header, int rows)
    {
        var path = Path.Combine(_csvFolder, Guid.NewGuid().ToString("N") + ".csv");
        var lines = new List<string> { header };
        for (var i = 1; i <= rows; i++)
            lines.Add($"{i},1,2024-01-01 00:00:00.000000,{new string('b', 32)},,s,10,10,tag,,0,png,,1,,100,0,,,2024-01-01 00:00:00.000000,f,f,f,0,0,0,f,f,f");
        File.WriteAllLines(path, lines);
        return path;
    }

    private long PostCount() => _db.Scalar(_db.Paths.ServerDatabasePath, "SELECT COUNT(*) FROM Posts;");

    [Fact]
    public async Task A_shrinking_import_is_refused_and_leaves_the_catalog_alone()
    {
        var result = await _importer.ImportPostsAsync(Csv(Header, 5));

        Assert.False(result.History.Succeeded);
        Assert.Contains("Refused", result.History.ErrorMessage);
        Assert.Equal(1000, PostCount());
    }

    [Fact]
    public async Task The_override_allows_one_import_and_is_then_spent()
    {
        _settings.Set(ImportSettingsKeys.ForceNextPostsImport, "true");

        var first = await _importer.ImportPostsAsync(Csv(Header, 5));
        Assert.True(first.History.Succeeded, first.History.ErrorMessage);
        Assert.Equal(5, PostCount());
        Assert.Equal("false", _settings.Get(ImportSettingsKeys.ForceNextPostsImport));
    }

    [Fact]
    public async Task The_override_is_spent_even_when_the_import_fails()
    {
        _settings.Set(ImportSettingsKeys.ForceNextPostsImport, "true");

        var failed = await _importer.ImportPostsAsync(Csv("id,md5", 3)); // header lacks required columns
        Assert.False(failed.History.Succeeded);
        Assert.Equal("false", _settings.Get(ImportSettingsKeys.ForceNextPostsImport));

        var next = await _importer.ImportPostsAsync(Csv(Header, 5));
        Assert.False(next.History.Succeeded); // protection is back
        Assert.Equal(1000, PostCount());
    }

    [Fact]
    public async Task A_normal_import_replaces_the_catalog()
    {
        var result = await _importer.ImportPostsAsync(Csv(Header, 1000));
        Assert.True(result.History.Succeeded, result.History.ErrorMessage);
        Assert.Equal(1000, PostCount());
    }
}
