using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;
using SpankBank.Core.Search;

namespace SpankBank.Data.Matching;

public sealed record MatchRunResult(int WatchesMatched, long PostsMatched, long RowsScanned, TimeSpan Elapsed, int PoolsAdded = 0);

/// <summary>
/// Works out which catalog posts each enabled watch matches and stores them in WatchMatches (SpankBank.sqlite).
/// Pools read their post list straight from the export; artists and searches share one pass over all posts.
/// </summary>
public sealed class WatchMatcher
{
    private readonly DatabasePaths _paths;
    private readonly string _serverConnectionString;
    private readonly string _appConnectionString;
    private readonly Action<string> _log;

    public WatchMatcher(DatabasePaths paths, Action<string>? log = null)
    {
        _paths = paths;
        _serverConnectionString = $"Data Source={paths.ServerDatabasePath};Mode=ReadOnly";
        _appConnectionString = $"Data Source={paths.AppDatabasePath}";
        _log = log ?? (_ => { });
    }

    /// <summary>Enabled watches that were added or changed since they were last matched.</summary>
    public int CountChanged()
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM WatchedItems WHERE Enabled = 1 AND LastCheckedAt IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <param name="all">Re-match every enabled watch; otherwise only watches marked changed (LastCheckedAt is NULL).</param>
    public MatchRunResult Run(bool all, CancellationToken cancellationToken = default, Action<long, long>? onScanProgress = null)
    {
        var started = DateTimeOffset.UtcNow;
        var watches = LoadWatches(all);
        if (watches.Count == 0)
            return new MatchRunResult(0, 0, 0, TimeSpan.Zero);

        long postsMatched = 0;
        long rowsScanned = 0;

        // Pools need no scan.
        foreach (var pool in watches.OfType<PoolWatch>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = LoadPoolPostIds(pool.PoolId);
            if (ids is null)
                Save(pool.Id, [], [], "Pool is not in the export yet.");
            else
                Save(pool.Id, ids, Enumerable.Range(0, ids.Count).ToList(), null);
            postsMatched += ids?.Count ?? 0;
        }

        var scanned = watches.OfType<ScanWatch>().ToList();
        if (scanned.Count > 0)
        {
            _log($"Matching {scanned.Count} watch(es) against the catalog...");
            rowsScanned = Scan(scanned, cancellationToken, onScanProgress);
            foreach (var watch in scanned)
            {
                Save(watch.Id, watch.Matches, null, watch.Error);
                postsMatched += watch.Matches.Count;
            }
        }

        var poolsAdded = 0;
        var artists = scanned.Where(w => w.ArtistName is not null && w.Error is null)
            .Select(w => new ArtistMatches(w.Id, w.ArtistName!, w.Matches)).ToList();
        if (artists.Count > 0)
        {
            try
            {
                poolsAdded = new PoolSuggester(_paths).Update(artists);
                if (poolsAdded > 0) _log($"Added {poolsAdded} pool(s) that are mostly a watched artist's work.");
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                _log($"Pool suggestions failed: {ex.Message}");
            }
        }

        return new MatchRunResult(watches.Count, postsMatched, rowsScanned, DateTimeOffset.UtcNow - started, poolsAdded);
    }

    // ---------------------------------------------------------------- loading watches

    private abstract class Watch(long id)
    {
        public long Id { get; } = id;
    }

    private sealed class PoolWatch(long id, long poolId) : Watch(id)
    {
        public long PoolId { get; } = poolId;
    }

    /// <summary>A watch matched by scanning posts: some tag terms plus a column-only query.</summary>
    private sealed class ScanWatch(long id) : Watch(id)
    {
        public string? ArtistName;        // set for artist watches: their pools are suggested afterwards
        public ArtistFilter? Filter;      // the artist's own whitelist/blacklist, when they have any
        public int[][] Required = [];     // each term: any of these tag indexes
        public int[][] Excluded = [];     // each term: none of these tag indexes
        public int[]? AnyOf;              // when set: at least one of these tag indexes
        public PostSearchQuery Query = PostSearchQuery.Parse("");
        public bool NeedsAnyTag;          // false for a search of column filters / exclusions only
        public string? Error;
        public List<long> Matches = [];
    }

    private List<Watch> LoadWatches(bool all)
    {
        var rows = new List<(long Id, WatchedItemType Type, string Source)>();
        var artistTags = new Dictionary<long, List<string>>();
        var filters = new Dictionary<long, ArtistFilter>();

        using (var connection = new SqliteConnection(_appConnectionString))
        {
            connection.Open();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT Id, ItemType, SourceValue, Whitelist, Blacklist FROM WatchedItems
                    WHERE Enabled = 1 {(all ? "" : "AND LastCheckedAt IS NULL")};
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), (WatchedItemType)reader.GetInt32(1), reader.GetString(2)));
                    var filter = ArtistFilter.Create(reader.GetString(3), reader.GetString(4));
                    if (!filter.IsEmpty) filters[reader.GetInt64(0)] = filter;
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT WatchedItemId, Tag FROM WatchedArtistTags WHERE Enabled = 1 AND Kind = $character;";
                cmd.Parameters.AddWithValue("$character", (int)ArtistTagKind.Character);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (!artistTags.TryGetValue(id, out var list)) artistTags[id] = list = [];
                    list.Add(reader.GetString(1));
                }
            }
        }

        var tagIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int Index(string tag)
        {
            if (!tagIndex.TryGetValue(tag, out var i)) tagIndex[tag] = i = tagIndex.Count;
            return i;
        }

        List<string>? allTagNames = null;
        int[] Expand(string term)
        {
            if (!term.Contains('*')) return [Index(term)];
            allTagNames ??= LoadAllTagNames();
            var regex = new Regex("^" + Regex.Escape(term).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);
            return allTagNames.Where(name => regex.IsMatch(name)).Select(Index).ToArray();
        }

        var watches = new List<Watch>();
        foreach (var (id, type, source) in rows)
        {
            switch (type)
            {
                case WatchedItemType.Pool when long.TryParse(source, out var poolId):
                    watches.Add(new PoolWatch(id, poolId));
                    break;

                case WatchedItemType.Artist:
                {
                    var tags = new List<string> { source };
                    if (artistTags.TryGetValue(id, out var characters)) tags.AddRange(characters);
                    watches.Add(new ScanWatch(id)
                    {
                        ArtistName = source,
                        // An artist's deleted posts are included: the downloader tries to recover them.
                        Query = PostSearchQuery.Parse("status:any"),
                        AnyOf = tags.SelectMany(Expand).Distinct().ToArray(),
                        NeedsAnyTag = true,
                        Filter = filters.GetValueOrDefault(id),
                    });
                    break;
                }

                case WatchedItemType.Search:
                {
                    var query = PostSearchQuery.Parse(source);
                    var problem = query.Error ?? (query.NeedsDatabase ? "A watch cannot use pool, parent/child or fav terms." : null);
                    var watch = new ScanWatch(id) { Query = query, Error = problem };
                    watch.Required = query.Required.Select(Expand).ToArray();
                    watch.Excluded = query.Excluded.Select(Expand).ToArray();
                    watch.AnyOf = query.AnyOf.Count > 0 ? query.AnyOf.SelectMany(Expand).Distinct().ToArray() : null;
                    // A query that failed to parse matches nothing rather than everything.
                    if (problem is null) watches.Add(watch); else MarkFailed(id, problem);
                    break;
                }

                default:
                    MarkFailed(id, "Unsupported watch.");
                    break;
            }
        }

        _tagIndex = tagIndex;
        return watches;
    }

    private Dictionary<string, int> _tagIndex = new();

    private List<string> LoadAllTagNames()
    {
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Tags;";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private List<long>? LoadPoolPostIds(long poolId)
    {
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PostIds FROM Pools WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", poolId);
        if (cmd.ExecuteScalar() is not string ids) return null;

        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => long.TryParse(id, out var value) ? value : (long?)null)
            .Where(id => id is not null).Select(id => id!.Value)
            .ToList();
    }

    // ---------------------------------------------------------------- the scan

    private long Scan(List<ScanWatch> watches, CancellationToken cancellationToken, Action<long, long>? onProgress)
    {
        var tagIndex = _tagIndex;
        var lookup = tagIndex.GetAlternateLookup<ReadOnlySpan<char>>();
        var present = new bool[tagIndex.Count];
        var touched = new List<int>(128);

        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();

        long totalRows;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM Posts;";
            totalRows = Convert.ToInt64(count.ExecuteScalar());
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, TagString, Rating, Score, FavCount, FileExt, ImageWidth, ImageHeight,
                   ParentId, FileSize, IsDeleted, CommentCount, CreatedAt, Md5
            FROM Posts;
            """;
        using var reader = cmd.ExecuteReader();

        long rows = 0;
        while (reader.Read())
        {
            if ((++rows & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke(rows, totalRows);
                if ((rows & 0xFFFFF) == 0) _log($"  scanned {rows:N0} posts...");
            }

            var tagString = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var tagCount = 0;
            foreach (var range in tagString.AsSpan().Split(' '))
            {
                var token = tagString.AsSpan(range);
                if (token.IsEmpty) continue;
                tagCount++;
                if (lookup.TryGetValue(token, out var index) && !present[index])
                {
                    present[index] = true;
                    touched.Add(index);
                }
            }

            var row = new PostMatchRow(
                Id: reader.GetInt64(0),
                Rating: reader.GetInt32(2),
                Score: reader.GetInt32(3),
                FavCount: reader.GetInt32(4),
                FileExt: reader.IsDBNull(5) ? null : reader.GetString(5),
                Width: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Height: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                ParentId: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                FileSize: reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                IsDeleted: reader.GetInt32(10) != 0,
                CommentCount: reader.GetInt32(11),
                CreatedAt: reader.GetString(12),
                Md5: reader.IsDBNull(13) ? null : reader.GetString(13),
                TagCount: tagCount);

            foreach (var watch in watches)
                if (Matches(watch, row, present) && (watch.Filter is null || watch.Filter.Allows(tagString, row)))
                    watch.Matches.Add(row.Id);

            foreach (var index in touched) present[index] = false;
            touched.Clear();
        }

        return rows;
    }

    private static bool Matches(ScanWatch watch, in PostMatchRow row, bool[] present)
    {
        if (watch.NeedsAnyTag && !AnyPresent(watch.AnyOf!, present)) return false;
        if (!watch.NeedsAnyTag && watch.AnyOf is { } anyOf && !AnyPresent(anyOf, present)) return false;

        foreach (var term in watch.Required)
            if (!AnyPresent(term, present)) return false;
        foreach (var term in watch.Excluded)
            if (AnyPresent(term, present)) return false;

        return watch.Query.MatchesRow(row);
    }

    private static bool AnyPresent(int[] indexes, bool[] present)
    {
        foreach (var index in indexes)
            if (present[index]) return true;
        return false;
    }

    // ---------------------------------------------------------------- storing results

    private void Save(long watchId, IReadOnlyList<long> postIds, IReadOnlyList<int>? positions, string? error)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM WatchMatches WHERE WatchedItemId = $id;";
            delete.Parameters.AddWithValue("$id", watchId);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO WatchMatches (WatchedItemId, PostId, Position) VALUES ($id, $post, $position);";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var post = insert.Parameters.Add("$post", SqliteType.Integer);
            var position = insert.Parameters.Add("$position", SqliteType.Integer);
            id.Value = watchId;
            insert.Prepare();
            for (var i = 0; i < postIds.Count; i++)
            {
                post.Value = postIds[i];
                position.Value = positions is null ? DBNull.Value : positions[i];
                insert.ExecuteNonQuery();
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE WatchedItems
                SET MatchCount = $count, LastCheckedAt = $now, ErrorMessage = $error, LastResultSummary = $summary
                WHERE Id = $id;
                """;
            update.Parameters.AddWithValue("$id", watchId);
            update.Parameters.AddWithValue("$count", postIds.Count);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            update.Parameters.AddWithValue("$summary", $"{postIds.Count:N0} posts");
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void MarkFailed(long watchId, string error) => Save(watchId, [], null, error);
}
