using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Repositories;

public sealed record FavoriteFolder(long Id, long? ParentId, string Name);

/// <summary>A rule of the folder tree was broken; the message is meant for the user.</summary>
public sealed class FavoriteFolderException(string message) : Exception(message);

/// <summary>
/// Nested folders for favorites (SpankBank.sqlite). Rules follow e621ng PR 2572: sibling names are unique ignoring case,
/// a folder cannot move into itself or its descendants, a favorite is in at most one folder, and deleting a folder
/// moves what it holds up to its parent.
/// </summary>
public sealed class FavoriteFolders
{
    private const int MaxNameLength = 100;
    private readonly string _connectionString;

    public FavoriteFolders(string appDatabasePath)
    {
        _connectionString = $"Data Source={appDatabasePath}";
    }

    public FavoriteFolder? Get(long id)
    {
        using var connection = Open();
        return Find(connection, id);
    }

    /// <summary>The folders directly inside a folder (null = the top level), A to Z.</summary>
    public IReadOnlyList<FavoriteFolder> Children(long? parentId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ParentId, Name FROM FavoriteFolders WHERE ParentId IS $parent ORDER BY lower(Name), Id;";
        cmd.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var folders = new List<FavoriteFolder>();
        while (reader.Read()) folders.Add(Read(reader));
        return folders;
    }

    public FavoriteFolder Create(string? name, long? parentId)
    {
        name = CleanName(name);
        using var connection = Open();
        if (parentId is { } parent && Find(connection, parent) is null) throw new FavoriteFolderException("The parent folder no longer exists.");
        EnsureNameFree(connection, name, parentId, exceptId: null);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO FavoriteFolders (ParentId, Name, CreatedAt) VALUES ($parent, $name, $now) RETURNING Id;";
        cmd.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        return new FavoriteFolder(Convert.ToInt64(cmd.ExecuteScalar()), parentId, name);
    }

    public FavoriteFolder Rename(long id, string? name)
    {
        name = CleanName(name);
        using var connection = Open();
        var folder = Find(connection, id) ?? throw new FavoriteFolderException("The folder no longer exists.");
        EnsureNameFree(connection, name, folder.ParentId, exceptId: id);

        Execute(connection, "UPDATE FavoriteFolders SET Name = $name WHERE Id = $id;", ("$name", name), ("$id", id));
        return folder with { Name = name };
    }

    public FavoriteFolder Move(long id, long? newParentId)
    {
        using var connection = Open();
        var folder = Find(connection, id) ?? throw new FavoriteFolderException("The folder no longer exists.");
        if (newParentId is { } target)
        {
            if (Find(connection, target) is null) throw new FavoriteFolderException("The destination folder no longer exists.");
            for (long? node = target; node is { } n; node = Find(connection, n)?.ParentId)
                if (n == id) throw new FavoriteFolderException("A folder cannot be moved into itself or one of its own folders.");
        }
        EnsureNameFree(connection, folder.Name, newParentId, exceptId: id);

        Execute(connection, "UPDATE FavoriteFolders SET ParentId = $parent WHERE Id = $id;", ("$parent", (object?)newParentId ?? DBNull.Value), ("$id", id));
        return folder with { ParentId = newParentId };
    }

    /// <summary>Deletes a folder; its favorites and folders move up to its parent. Fails when a folder inside has the same name as one already in the parent.</summary>
    public long? Delete(long id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var folder = Find(connection, id) ?? throw new FavoriteFolderException("The folder no longer exists.");

        using (var clash = connection.CreateCommand())
        {
            clash.Transaction = transaction;
            clash.CommandText = """
                SELECT c.Name FROM FavoriteFolders c
                JOIN FavoriteFolders s ON s.Id <> $id AND s.ParentId IS $parent AND lower(s.Name) = lower(c.Name)
                WHERE c.ParentId = $id LIMIT 1;
                """;
            clash.Parameters.AddWithValue("$id", id);
            clash.Parameters.AddWithValue("$parent", (object?)folder.ParentId ?? DBNull.Value);
            if (clash.ExecuteScalar() is string name)
                throw new FavoriteFolderException($"\"{name}\" inside this folder would clash with a folder of the same name one level up. Rename it first.");
        }

        // The folder's own row goes first: a folder inside it may share its name, which would clash with it at the new level.
        // The foreign keys are checked at commit, by which time the children point elsewhere.
        Execute(connection, transaction, "PRAGMA defer_foreign_keys = ON;");
        Execute(connection, transaction, "DELETE FROM FavoriteFolders WHERE Id = $id;", ("$id", id));
        Execute(connection, transaction, "UPDATE FavoriteFolders SET ParentId = $parent WHERE ParentId = $id;", ("$parent", (object?)folder.ParentId ?? DBNull.Value), ("$id", id));
        if (folder.ParentId is { } up)
            Execute(connection, transaction, "UPDATE FavoriteFolderPosts SET FolderId = $parent WHERE FolderId = $id;", ("$parent", up), ("$id", id));
        else
            Execute(connection, transaction, "DELETE FROM FavoriteFolderPosts WHERE FolderId = $id;", ("$id", id)); // at the top level, "unfiled" is no row
        transaction.Commit();
        return folder.ParentId;
    }

    /// <summary>Files a favorite into a folder, or back to the top level with null.</summary>
    public void MovePost(long postId, long? folderId)
    {
        using var connection = Open();
        if (Query(connection, "SELECT 1 FROM Favorites WHERE PostId = $id;", ("$id", postId)) is null)
            throw new FavoriteFolderException("Only favorites can be filed into folders.");

        if (folderId is not { } folder)
        {
            Execute(connection, "DELETE FROM FavoriteFolderPosts WHERE PostId = $id;", ("$id", postId));
            return;
        }
        if (Find(connection, folder) is null) throw new FavoriteFolderException("The folder no longer exists.");
        Execute(connection, "INSERT INTO FavoriteFolderPosts (PostId, FolderId) VALUES ($id, $folder) ON CONFLICT(PostId) DO UPDATE SET FolderId = excluded.FolderId;",
            ("$id", postId), ("$folder", folder));
    }

    private static string CleanName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new FavoriteFolderException("Folder name is required.");
        if (name.Length > MaxNameLength) throw new FavoriteFolderException($"Folder name is too long (at most {MaxNameLength} characters).");
        return name;
    }

    private static void EnsureNameFree(SqliteConnection connection, string name, long? parentId, long? exceptId)
    {
        var taken = Query(connection, "SELECT 1 FROM FavoriteFolders WHERE ParentId IS $parent AND lower(Name) = lower($name) AND Id IS NOT $except;",
            ("$parent", (object?)parentId ?? DBNull.Value), ("$name", name), ("$except", (object?)exceptId ?? DBNull.Value));
        if (taken is not null) throw new FavoriteFolderException($"A folder named \"{name}\" already exists here.");
    }

    private static FavoriteFolder? Find(SqliteConnection connection, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ParentId, Name FROM FavoriteFolders WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static FavoriteFolder Read(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static object? Query(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        Execute(connection, null, sql, parameters);

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
