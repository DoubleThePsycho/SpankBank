using Microsoft.Data.Sqlite;
using SpankBank.Data.Blacklist;
using SpankBank.Data.Repositories;

namespace SpankBank.Data.Downloads;

/// <summary>What to do with a file the user offers for a post id.</summary>
public enum ImportVerdict
{
    /// <summary>Deleted (or lost) on the site and not on this device: the file is wanted.</summary>
    Wanted,
    NotInCatalog,
    /// <summary>A file for the post is already on this device.</summary>
    AlreadyLocal,
    /// <summary>In the rubbish bin, or deleted by the user (a purged one stays unwanted).</summary>
    Binned,
    /// <summary>The site still has the post and an enabled watch matches it, so the downloader fetches the real original.</summary>
    StillOnline,
    /// <summary>The site still has the post and no enabled watch matches it, so nothing will download it.</summary>
    NotWatched,
    /// <summary>A blacklist line matches the post (and no watch that ignores the blacklist matches it, enabled or not: you set that on purpose).</summary>
    Blacklisted,
    /// <summary>Deleted as a duplicate or inferior version of another post (only with the "skip inferior" setting).</summary>
    Inferior,
    /// <summary>Deleted as a trivial or low-quality edit (only with the "skip inferior" setting).</summary>
    LowEffortEdit,
    /// <summary>With the "skip inferior" setting on, the deletion reason has not been looked up yet.</summary>
    ReasonUnknown,
}

/// <param name="Reason">The deletion reason (plain text), when known.</param>
public sealed record ImportCheck(long PostId, ImportVerdict Verdict, string Md5, string? Reason)
{
    public string Message => Verdict switch
    {
        ImportVerdict.Wanted => "wanted",
        ImportVerdict.NotInCatalog => "not in the catalog",
        ImportVerdict.AlreadyLocal => "already on this device",
        ImportVerdict.Binned => "in the rubbish bin, or deleted by you",
        ImportVerdict.StillOnline => "not deleted on the site; an enabled watch matches it, so the downloader gets the original",
        ImportVerdict.NotWatched => "not deleted on the site and no enabled watch matches it (watch its artist or tag to download it)",
        ImportVerdict.Blacklisted => "matches a blacklist line",
        ImportVerdict.Inferior => "deleted as an inferior version" + Because + " (the \"skip inferior\" setting is on)",
        ImportVerdict.LowEffortEdit => "deleted as a low-effort edit" + Because + " (the \"skip inferior\" setting is on)",
        _ => "the deletion reason is not known yet",
    };

    private string Because => string.IsNullOrWhiteSpace(Reason) ? "" : ": " + Reason;
}

/// <summary>
/// Decides, per post id, whether a file the user brings from their PC is wanted: only posts the site deleted (or the downloader
/// found lost) and this device does not have. Works together with the "skip inferior versions and low-effort edits" setting:
/// with it on, posts deleted as such are refused, and posts whose reason is not stored yet come back as
/// <see cref="ImportVerdict.ReasonUnknown"/> so the caller can look it up first.
/// </summary>
public sealed class ImportChecker
{
    private const int ChunkSize = 500;
    private readonly DatabasePaths _paths;

    public ImportChecker(DatabasePaths paths) => _paths = paths;

    public IReadOnlyDictionary<long, ImportCheck> Check(IReadOnlyCollection<long> postIds, bool skipInferior)
    {
        var results = new Dictionary<long, ImportCheck>();
        var ids = postIds.Distinct().ToList();
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath};Pooling=False");
        connection.Open();
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = $"ATTACH DATABASE '{_paths.DeviceDatabasePath.Replace("'", "''")}' AS dev; ATTACH DATABASE '{_paths.ServerDatabasePath.Replace("'", "''")}' AS srv;";
            attach.ExecuteNonQuery();
        }

        var entries = new List<string>();
        using (var lines = connection.CreateCommand())
        {
            lines.CommandText = "SELECT Query FROM Blacklist ORDER BY Id;";
            using var reader = lines.ExecuteReader();
            while (reader.Read()) entries.Add(reader.GetString(0));
        }
        var rules = BlacklistRules.Create(entries);

        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            var names = chunk.Select((_, i) => $"$i{i}").ToList();
            // Columns 0-13 are what the blacklist tests (see BlockedPostsRefresher.ReadRow); the flags follow.
            cmd.CommandText = $"""
                SELECT p.Id, p.TagString, p.Rating, p.Score, p.FavCount, p.FileExt, p.ImageWidth, p.ImageHeight, p.ParentId, p.FileSize,
                       p.IsDeleted, p.CommentCount, p.CreatedAt, p.Md5,
                       d.Id IS NOT NULL, g.PostId IS NOT NULL, x.PostId IS NOT NULL, r.PostId IS NOT NULL, r.Kind, r.Reason,
                       EXISTS (SELECT 1 FROM WatchMatches m JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.Enabled = 1 WHERE m.PostId = p.Id),
                       EXISTS (SELECT 1 FROM WatchMatches m JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.IgnoreBlacklist = 1 WHERE m.PostId = p.Id)
                FROM srv.Posts p
                LEFT JOIN dev.Posts d ON d.Id = p.Id AND d.SavedAt IS NOT NULL
                LEFT JOIN DeletedPosts g ON g.PostId = p.Id
                LEFT JOIN DownloadAttempts x ON x.PostId = p.Id AND {WatchesRepository.LostCondition}
                LEFT JOIN DeletionReasons r ON r.PostId = p.Id
                WHERE p.Id IN ({string.Join(", ", names)});
                """;
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = BlockedPostsRefresher.ReadRow(reader, out var tagString);
                var md5 = row.Md5 ?? "";
                var (local, binned, lost, hasReason) = (reader.GetInt32(14) != 0, reader.GetInt32(15) != 0, reader.GetInt32(16) != 0, reader.GetInt32(17) != 0);
                var kind = reader.IsDBNull(18) ? null : reader.GetString(18);
                var reason = reader.IsDBNull(19) ? null : DeletionReasons.PlainText(reader.GetString(19));
                var (watched, exempt) = (reader.GetInt32(20) != 0, reader.GetInt32(21) != 0);
                var blocked = !exempt && rules.Count > 0 && rules.Matches(tagString, row);

                var verdict = local ? ImportVerdict.AlreadyLocal
                    : binned ? ImportVerdict.Binned
                    : blocked ? ImportVerdict.Blacklisted
                    : !row.IsDeleted && !lost ? (watched ? ImportVerdict.StillOnline : ImportVerdict.NotWatched)
                    : md5.Length != 32 ? ImportVerdict.NotInCatalog
                    : !skipInferior ? ImportVerdict.Wanted
                    : !hasReason ? ImportVerdict.ReasonUnknown
                    : kind == "inferior" ? ImportVerdict.Inferior
                    : kind == "edit" ? ImportVerdict.LowEffortEdit
                    : ImportVerdict.Wanted;
                results[row.Id] = new ImportCheck(row.Id, verdict, md5, reason);
            }
        }

        foreach (var id in ids.Where(id => !results.ContainsKey(id)))
            results[id] = new ImportCheck(id, ImportVerdict.NotInCatalog, "", null);
        return results;
    }
}
