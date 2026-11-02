using SpankBank.Core.Search;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

/// <summary>The advanced-search media and sound filters write tag groups such as <c>( ~video ~flash ) sound</c>.</summary>
public class PostSearchGroupsTests : IDisposable
{
    private readonly TempDatabases _db = new();

    public PostSearchGroupsTests()
    {
        // 1 image, 2 gif, 3 video with sound, 4 flash, 5 video without sound
        var tags = new[] { "solo", "solo animated_gif", "solo video sound sound_warning", "solo flash", "solo video no_sound" };
        for (var i = 0; i < tags.Length; i++)
            _db.Execute(_db.Paths.DeviceDatabasePath,
                $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, SavedAt, Status, TagString) VALUES ({i + 1}, '2024-01-01', '2024-01-01', '{new string((char)('a' + i), 32)}', 'png', '2026-01-01T00:00:00+00:00', 1, '{tags[i]}');");
    }

    public void Dispose() => _db.Dispose();

    private long[] Found(string query) => new DownloadedPostSearch(_db.Paths).Search(query, 1, 50).Items.Select(i => i.Id).Order().ToArray();

    [Fact] public void A_group_matches_posts_with_any_of_its_tags() => Assert.Equal([3L, 4L, 5L], Found("( ~video ~flash )"));
    [Fact] public void Groups_combine_with_plain_terms() => Assert.Equal([3L], Found("( ~video ~flash ) sound"));
    [Fact] public void Image_only_is_written_with_exclusions() => Assert.Equal([1L], Found("-animated_gif -animated_png -animated_webp -video -flash"));
    [Fact] public void Two_groups_both_have_to_match() => Assert.Equal([3L], Found("( ~video ~flash ) ( ~sound ~sound_warning )"));
    [Fact] public void Unbalanced_parentheses_are_reported_not_ignored() => Assert.NotNull(new DownloadedPostSearch(_db.Paths).Search("( ~video ~flash", 1, 50).Error);
}
