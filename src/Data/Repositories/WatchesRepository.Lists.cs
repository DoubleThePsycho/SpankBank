using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

/// <summary>Downloaded / restored / lost post counts of one watch (see <see cref="WatchesRepository.GetPostStats"/>).</summary>
/// <param name="Downloaded">Originals fetched from e621.</param>
/// <param name="Restored">Recovered from the Wayback Machine (the original, or only e621's sample).</param>
/// <param name="Lost">Deleted on e621 and not found anywhere, so they cannot be had.</param>
public sealed record WatchPostStats(int Downloaded, int Restored, int Lost)
{
    public static readonly WatchPostStats None = new(0, 0, 0);
    public int Local => Downloaded + Restored;
}

/// <param name="Found">Character tags discovered automatically (not added by hand).</param>
/// <param name="Enabled">Characters that are switched on, found or custom.</param>
/// <param name="Custom">Characters the user added by hand.</param>
public sealed record ArtistListItem(WatchEntry Entry, int Aliases, int Found, int Enabled, int Custom, WatchPostStats Stats);

public sealed record WatchedPoolItem(WatchEntry Entry, int PagesInExport, WatchPostStats Stats);

public sealed record WatchPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int LastPage => Math.Max(1, (int)Math.Ceiling(Total / (double)Math.Max(1, PageSize)));
}

public sealed partial class WatchesRepository
{
    public const int DefaultListPageSize = 100;

    // Failures the downloader records for a post that e621 deleted and no archive has.
    private const string LostCondition = "(x.LastError LIKE 'Deleted on e621 and not found%' OR x.LastError LIKE 'The catalog has no file%')";

    /// <summary>
    /// Per watch of a type: how many of its matched posts are downloaded, restored (Wayback) or lost. One pass over the matches.
    /// </summary>
    public Dictionary<long, WatchPostStats> GetPostStats(WatchedItemType type, IReadOnlyCollection<long>? watchIds = null)
    {
        using var connection = OpenWithDevice();
        using var cmd = connection.CreateCommand();
        var idFilter = "";
        if (watchIds is { Count: > 0 })
        {
            var placeholders = watchIds.Select((_, i) => $"$w{i}").ToList();
            idFilter = $" AND w.Id IN ({string.Join(", ", placeholders)})";
            var n = 0;
            foreach (var id in watchIds) cmd.Parameters.AddWithValue(placeholders[n++], id);
        }
        else if (watchIds is { Count: 0 }) return [];

        cmd.CommandText = $"""
            SELECT m.WatchedItemId,
                   COALESCE(SUM(d.Status = 1), 0),
                   COALESCE(SUM(d.Status IN (2, 3)), 0),
                   COALESCE(SUM(d.Id IS NULL AND x.PostId IS NOT NULL), 0)
            FROM WatchMatches m
            JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.ItemType = $type{idFilter}
            LEFT JOIN dev.Posts d ON d.Id = m.PostId AND d.SavedAt IS NOT NULL
            LEFT JOIN DownloadAttempts x ON x.PostId = m.PostId AND {LostCondition}
            GROUP BY m.WatchedItemId;
            """;
        cmd.Parameters.AddWithValue("$type", (int)type);
        var stats = new Dictionary<long, WatchPostStats>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            stats[reader.GetInt64(0)] = new WatchPostStats(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        return stats;
    }

    /// <param name="sort">id, name, aliases, found, enabled, custom, total, downloaded, restored or lost (default name).</param>
    public WatchPage<ArtistListItem> GetArtistPage(string? sort, bool descending, int page, int pageSize)
    {
        var all = GetAll(WatchedItemType.Artist);
        var stats = GetPostStats(WatchedItemType.Artist);
        var items = all.Select(e =>
        {
            var characters = e.ArtistTags.Where(t => t.Kind == ArtistTagKind.Character).ToList();
            return new ArtistListItem(e, e.ArtistTags.Count(t => t.Kind == ArtistTagKind.Alias), characters.Count(c => !c.IsManual),
                characters.Count(c => c.Enabled), characters.Count(c => c.IsManual), stats.GetValueOrDefault(e.Id, WatchPostStats.None));
        });

        Func<ArtistListItem, IComparable> key = sort switch
        {
            "id" => i => i.Entry.Id,
            "aliases" => i => i.Aliases,
            "found" => i => i.Found,
            "enabled" => i => i.Enabled,
            "custom" => i => i.Custom,
            "total" => i => i.Entry.MatchCount,
            "downloaded" => i => i.Stats.Downloaded,
            "restored" => i => i.Stats.Restored,
            "lost" => i => i.Stats.Lost,
            _ => i => i.Entry.SourceValue.ToLowerInvariant(),
        };
        var ordered = descending ? items.OrderByDescending(key) : items.OrderBy(key);
        var list = ordered.ThenBy(i => i.Entry.SourceValue, StringComparer.OrdinalIgnoreCase).ToList();

        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);
        return new WatchPage<ArtistListItem>(list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, page, pageSize);
    }

    /// <param name="sort">id (default), name, artist, type, pages, local or status.</param>
    /// <param name="includeDisabled">Disabled pool watches are left out of the list unless asked for.</param>
    public WatchPage<WatchedPoolItem> GetPoolPage(string? sort, bool descending, int page, int pageSize, bool includeDisabled)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);
        var direction = descending ? "DESC" : "ASC";
        var orderBy = sort switch
        {
            "name" => $"COALESCE(NULLIF(w.LocalName, ''), p.Name, w.DisplayName) COLLATE NOCASE {direction}",
            "artist" => $"COALESCE(w.Origin, '') COLLATE NOCASE {direction}",
            "type" => $"COALESCE(p.Category, '') {direction}",
            "pages" => $"COALESCE(p.PostCount, 0) {direction}",
            "status" => $"w.Enabled {direction}",
            "local" => $"(SELECT COUNT(*) FROM WatchMatches lm JOIN dev.Posts ld ON ld.Id = lm.PostId AND ld.SavedAt IS NOT NULL WHERE lm.WatchedItemId = w.Id) {direction}",
            _ => $"CAST(w.SourceValue AS INTEGER) {direction}",
        };
        var where = includeDisabled ? "w.ItemType = 0" : "w.ItemType = 0 AND w.Enabled = 1";

        using var connection = OpenWithDevice(attachServer: true);
        int total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM WatchedItems w WHERE {where};";
            total = Convert.ToInt32(count.ExecuteScalar());
        }

        var entries = new List<WatchEntry>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT w.Id, w.SourceValue, w.DisplayName, w.Enabled, w.LocalName, w.MatchCount, w.LastCheckedAt IS NOT NULL,
                       w.ErrorMessage, w.Origin, w.IgnoreBlacklist, p.Name, p.Category, p.PostCount
                FROM WatchedItems w LEFT JOIN srv.Pools p ON p.Id = CAST(w.SourceValue AS INTEGER)
                WHERE {where}
                ORDER BY {orderBy}, CAST(w.SourceValue AS INTEGER) DESC
                LIMIT $limit OFFSET $offset;
                """;
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                PoolInfo? pool = reader.IsDBNull(10) ? null : new PoolInfo(reader.GetString(10), reader.GetString(11), reader.GetInt32(12));
                var localName = reader.IsDBNull(4) ? null : reader.GetString(4);
                var display = !string.IsNullOrWhiteSpace(localName) ? localName
                    : pool is not null ? pool.Name.Replace('_', ' ')
                    : reader.GetString(2);
                entries.Add(new WatchEntry(reader.GetInt64(0), WatchedItemType.Pool, reader.GetString(1), display, reader.GetInt32(3) != 0,
                    [], localName, pool, reader.GetInt32(5), reader.GetInt32(6) != 0, reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), 0, reader.GetInt32(9) != 0));
            }
        }

        var stats = GetPostStats(WatchedItemType.Pool, entries.Select(e => e.Id).ToList());
        var items = entries.Select(e => new WatchedPoolItem(e, e.Pool?.PostCount ?? 0, stats.GetValueOrDefault(e.Id, WatchPostStats.None))).ToList();
        return new WatchPage<WatchedPoolItem>(items, total, page, pageSize);
    }

    public int CountDisabledPools()
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM WatchedItems WHERE ItemType = 0 AND Enabled = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>The pool watches that were added for this artist (automatically or from a suggestion), with their post counts.</summary>
    public IReadOnlyList<WatchedPoolItem> GetArtistPools(string artistTag)
    {
        var origins = new[] { $"auto:{artistTag}", $"suggested:{artistTag}" };
        var entries = GetAll(WatchedItemType.Pool).Where(p => p.Origin is { } o && origins.Contains(o)).ToList();
        var stats = GetPostStats(WatchedItemType.Pool, entries.Select(e => e.Id).ToList());
        return entries.Select(e => new WatchedPoolItem(e, e.Pool?.PostCount ?? 0, stats.GetValueOrDefault(e.Id, WatchPostStats.None)))
            .OrderByDescending(p => long.TryParse(p.Entry.SourceValue, out var id) ? id : 0).ToList();
    }

    /// <summary>
    /// Removes pool watches that were added for a marker tag treated as an artist (unknown_artist, conditional_dnp...).
    /// Posts already downloaded stay; any that nothing else keeps become strays.
    /// </summary>
    /// <returns>How many pool watches were removed.</returns>
    public int RemovePoolsOfNonArtists()
    {
        var origins = NonArtistTags.Names.SelectMany(n => new[] { $"'auto:{n}'", $"'suggested:{n}'" });
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM WatchedItems WHERE ItemType = 0 AND Origin IN ({string.Join(", ", origins)});";
        return cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenWithDevice(bool attachServer = false)
    {
        var connection = new SqliteConnection(_appConnectionString + ";Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{_devicePath.Replace("'", "''")}' AS dev;";
        cmd.ExecuteNonQuery();
        if (attachServer)
        {
            cmd.CommandText = $"ATTACH DATABASE '{_serverPath.Replace("'", "''")}' AS srv;";
            cmd.ExecuteNonQuery();
        }
        return connection;
    }
}
