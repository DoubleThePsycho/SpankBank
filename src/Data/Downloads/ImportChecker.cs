using Microsoft.Data.Sqlite;
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
    /// <summary>The site still has the post, so the downloader fetches the real original.</summary>
    StillOnline,
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
        ImportVerdict.StillOnline => "not deleted on the site (not downloaded yet), so the original is not replaced",
        ImportVerdict.Blacklisted => "hidden by the blacklist",
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

        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            var names = chunk.Select((_, i) => $"$i{i}").ToList();
            cmd.CommandText = $"""
                SELECT p.Id, COALESCE(p.Md5, ''), p.IsDeleted, d.Id IS NOT NULL, g.PostId IS NOT NULL, b.PostId IS NOT NULL, x.PostId IS NOT NULL,
                       r.PostId IS NOT NULL, r.Kind, r.Reason
                FROM srv.Posts p
                LEFT JOIN dev.Posts d ON d.Id = p.Id AND d.SavedAt IS NOT NULL
                LEFT JOIN DeletedPosts g ON g.PostId = p.Id
                LEFT JOIN BlockedPosts b ON b.PostId = p.Id
                LEFT JOIN DownloadAttempts x ON x.PostId = p.Id AND {WatchesRepository.LostCondition}
                LEFT JOIN DeletionReasons r ON r.PostId = p.Id
                WHERE p.Id IN ({string.Join(", ", names)});
                """;
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var md5 = reader.GetString(1);
                var (deleted, local, binned, blocked, lost, hasReason) = (reader.GetInt32(2) != 0, reader.GetInt32(3) != 0, reader.GetInt32(4) != 0, reader.GetInt32(5) != 0, reader.GetInt32(6) != 0, reader.GetInt32(7) != 0);
                var kind = reader.IsDBNull(8) ? null : reader.GetString(8);
                var reason = reader.IsDBNull(9) ? null : DeletionReasons.PlainText(reader.GetString(9));

                var verdict = local ? ImportVerdict.AlreadyLocal
                    : binned ? ImportVerdict.Binned
                    : !deleted && !lost ? ImportVerdict.StillOnline
                    : blocked ? ImportVerdict.Blacklisted
                    : md5.Length != 32 ? ImportVerdict.NotInCatalog
                    : !skipInferior ? ImportVerdict.Wanted
                    : !hasReason ? ImportVerdict.ReasonUnknown
                    : kind == "inferior" ? ImportVerdict.Inferior
                    : kind == "edit" ? ImportVerdict.LowEffortEdit
                    : ImportVerdict.Wanted;
                results[id] = new ImportCheck(id, verdict, md5, reason);
            }
        }

        foreach (var id in ids.Where(id => !results.ContainsKey(id)))
            results[id] = new ImportCheck(id, ImportVerdict.NotInCatalog, "", null);
        return results;
    }
}
