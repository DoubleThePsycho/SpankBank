using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Repositories;

/// <summary>
/// How many posts on this device carry each tag, for the "local" column next to a tag's online (export) count.
/// Counted from the posts' tag strings in one pass and then kept in memory: the first call builds it (a few seconds with
/// hundreds of thousands of posts), later calls only look up. At most every <see cref="DefaultRefreshAfter"/> a call checks
/// whether posts were added or removed; added posts are counted in on top, anything else triggers a rebuild, in the
/// background, while the old numbers keep being served. A tag nobody has locally counts 0.
/// </summary>
public sealed class LocalTagCounts
{
    public static readonly TimeSpan DefaultRefreshAfter = TimeSpan.FromSeconds(30);

    private readonly DatabasePaths _paths;
    private readonly object _gate = new();
    private volatile Snapshot? _snapshot;
    private Task? _refreshing;
    private readonly TimeSpan _refreshAfter;
    private DateTime _checkedAt = DateTime.MinValue;

    public LocalTagCounts(DatabasePaths paths, TimeSpan? refreshAfter = null)
    {
        _paths = paths;
        _refreshAfter = refreshAfter ?? DefaultRefreshAfter;
    }

    /// <summary>Builds the counts now (call it at start so the first page does not wait for it).</summary>
    public void Warm() => Current();

    public int Get(string tag) => Current().Counts.GetValueOrDefault(tag);

    /// <summary>The local count of each given tag (0 when absent).</summary>
    public IReadOnlyDictionary<string, int> Get(IEnumerable<string> tags)
    {
        var counts = Current().Counts;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tag in tags) result[tag] = counts.GetValueOrDefault(tag);
        return result;
    }

    private Snapshot Current()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            lock (_gate)
            {
                _snapshot ??= Build();
                _checkedAt = DateTime.UtcNow;
                return _snapshot;
            }
        }

        if (DateTime.UtcNow - _checkedAt >= _refreshAfter)
        {
            lock (_gate)
            {
                if (_refreshing is null || _refreshing.IsCompleted)
                {
                    _checkedAt = DateTime.UtcNow;
                    _refreshing = Task.Run(Refresh);
                }
            }
        }
        return snapshot;
    }

    private void Refresh()
    {
        try
        {
            var old = _snapshot!;
            using var connection = Open();
            var (total, newest) = Totals(connection);
            if (total == old.Posts && newest == old.Newest) return;

            // Only additions since the last look: the posts saved after the newest one seen, and nothing else changed.
            if (old.Newest is not null && newest is not null && string.CompareOrdinal(newest, old.Newest) >= 0)
            {
                var counts = new Dictionary<string, int>(old.Counts, StringComparer.Ordinal);
                var added = CountInto(connection, counts, old.Newest);
                if (old.Posts + added == total)
                {
                    _snapshot = new Snapshot(counts, total, newest);
                    return;
                }
            }
            _snapshot = Build(connection);
        }
        catch (Exception)
        {
            // Busy or half-written database: keep the old numbers and look again after the next interval.
        }
    }

    private Snapshot Build()
    {
        using var connection = Open();
        return Build(connection);
    }

    private static Snapshot Build(SqliteConnection connection)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        CountInto(connection, counts, after: null);
        var (total, newest) = Totals(connection);
        return new Snapshot(counts, total, newest);
    }

    private static (long Total, string? Newest) Totals(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(SavedAt) FROM Posts WHERE SavedAt IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>Counts the tags of the saved posts (those saved after the given time, if any); returns how many posts that was.</summary>
    private static long CountInto(SqliteConnection connection, Dictionary<string, int> counts, string? after)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(s.TagString, d.TagString, '')
            FROM Posts d LEFT JOIN srv.Posts s ON s.Id = d.Id
            WHERE d.SavedAt IS NOT NULL{(after is null ? "" : " AND d.SavedAt > $after")};
            """;
        if (after is not null) cmd.Parameters.AddWithValue("$after", after);

        long posts = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            posts++;
            var text = reader.GetString(0).AsSpan();
            while (!text.IsEmpty)
            {
                var space = text.IndexOf(' ');
                var token = space < 0 ? text : text[..space];
                text = space < 0 ? default : text[(space + 1)..];
                if (token.IsEmpty) continue;
                var key = token.ToString();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        return posts;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{_paths.ServerDatabasePath.Replace("'", "''")}' AS srv;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private sealed record Snapshot(Dictionary<string, int> Counts, long Posts, string? Newest);
}
