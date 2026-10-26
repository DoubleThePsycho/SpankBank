using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class FavoriteTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly FavoriteFolders _folders;
    private readonly FavoritesRepository _favorites;

    public FavoriteTests()
    {
        _folders = new FavoriteFolders(_db.Paths.AppDatabasePath);
        _favorites = new FavoritesRepository(_db.Paths.AppDatabasePath);
    }

    public void Dispose() => _db.Dispose();

    private long? FolderOf(long postId)
    {
        var count = _db.Scalar(_db.Paths.AppDatabasePath, $"SELECT COUNT(*) FROM FavoriteFolderPosts WHERE PostId = {postId};");
        return count == 0 ? null : _db.Scalar(_db.Paths.AppDatabasePath, $"SELECT FolderId FROM FavoriteFolderPosts WHERE PostId = {postId};");
    }

    [Fact]
    public void Sibling_names_are_unique_ignoring_case()
    {
        _folders.Create("Alpha", null);
        Assert.Throws<FavoriteFolderException>(() => _folders.Create("ALPHA", null));
    }

    [Fact]
    public void The_same_name_is_fine_under_different_parents()
    {
        var a = _folders.Create("A", null);
        var b = _folders.Create("B", null);
        _folders.Create("x", a.Id);
        _folders.Create("x", b.Id);
    }

    [Fact]
    public void A_folder_cannot_move_into_itself_or_a_descendant()
    {
        var a = _folders.Create("A", null);
        var b = _folders.Create("B", a.Id);
        Assert.Throws<FavoriteFolderException>(() => _folders.Move(a.Id, a.Id));
        Assert.Throws<FavoriteFolderException>(() => _folders.Move(a.Id, b.Id));
    }

    [Fact]
    public void A_favorite_lives_in_at_most_one_folder()
    {
        var a = _folders.Create("A", null);
        var b = _folders.Create("B", null);
        _favorites.Add(10);
        _folders.MovePost(10, a.Id);
        _folders.MovePost(10, b.Id);

        Assert.Equal(b.Id, FolderOf(10));
        Assert.Equal(1, _db.Scalar(_db.Paths.AppDatabasePath, "SELECT COUNT(*) FROM FavoriteFolderPosts WHERE PostId = 10;"));
    }

    [Fact]
    public void Only_favorites_can_be_filed()
    {
        var a = _folders.Create("A", null);
        Assert.Throws<FavoriteFolderException>(() => _folders.MovePost(99, a.Id));
    }

    [Fact]
    public void Unfavoriting_removes_the_folder_membership()
    {
        var a = _folders.Create("A", null);
        _favorites.Add(10);
        _folders.MovePost(10, a.Id);
        _favorites.Remove(10);

        Assert.Null(FolderOf(10));
        Assert.False(_favorites.IsFavorite(10));
    }

    [Fact]
    public void Deleting_a_folder_promotes_its_folders_and_posts()
    {
        var a = _folders.Create("A", null);
        var b = _folders.Create("B", a.Id);
        var c = _folders.Create("C", b.Id);
        _favorites.Add(10);
        _folders.MovePost(10, b.Id);

        Assert.Equal(a.Id, _folders.Delete(b.Id));

        Assert.Null(_folders.Get(b.Id));
        Assert.Equal(a.Id, _folders.Get(c.Id)!.ParentId);
        Assert.Equal(a.Id, FolderOf(10));
    }

    [Fact]
    public void Deleting_a_top_level_folder_unfiles_its_posts_but_keeps_them_favorited()
    {
        var a = _folders.Create("A", null);
        _favorites.Add(10);
        _folders.MovePost(10, a.Id);
        _folders.Delete(a.Id);

        Assert.Null(FolderOf(10));
        Assert.True(_favorites.IsFavorite(10));
    }

    [Fact]
    public void Deleting_is_refused_when_a_promoted_folder_would_clash()
    {
        var a = _folders.Create("A", null);
        var b = _folders.Create("B", a.Id);
        _folders.Create("same", a.Id);
        _folders.Create("same", b.Id);

        Assert.Throws<FavoriteFolderException>(() => _folders.Delete(b.Id));
        Assert.NotNull(_folders.Get(b.Id));
    }

    [Fact]
    public void A_child_may_share_its_parents_name_when_the_parent_is_deleted()
    {
        var beta = _folders.Create("Beta", null);
        var inner = _folders.Create("beta", beta.Id);

        _folders.Delete(beta.Id);
        Assert.Null(_folders.Get(inner.Id)!.ParentId);
    }
}
