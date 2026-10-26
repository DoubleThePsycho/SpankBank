using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Matching;

/// <summary>An artist watch and the catalog posts it matched.</summary>
public sealed record ArtistMatches(long WatchId, string Artist, IReadOnlyList<long> PostIds);

/// <summary>
/// Finds the pools that contain a watched artist's posts. Pools that are mostly the artist's work are added to the
/// watches automatically; the rest are kept as suggestions to review on the Pools page.
/// A pool is skipped as a sub-pool when most of the artist's posts in it also sit in a larger pool (as the old script did).
/// </summary>
public sealed class PoolSuggester
{
    public const int DefaultAutoAddPercent = 90;
    private const int MinSharedPosts = 2;
    private const double SubPoolThreshold = 0.90;

    private readonly string _serverConnectionString;
    private readonly string _appConnectionString;

    public PoolSuggester(DatabasePaths paths)
    {
        _serverConnectionString = $"Data Source={paths.ServerDatabasePath};Mode=ReadOnly";
        _appConnectionString = $"Data Source={paths.AppDatabasePath}";
    }

    private sealed record Pool(long Id, string Category, long[] PostIds);

    private sealed class Candidate(Pool pool, HashSet<long> artistPosts)
    {
        public Pool Pool { get; } = pool;
        public HashSet<long> ArtistPosts { get; } = artistPosts;
        public bool IsRoot { get; set; } = true;
    }

    /// <returns>How many pools were added to the watches.</returns>
    public int Update(IReadOnlyList<ArtistMatches> artists)
    {
        if (artists.Count == 0) return 0;

        var (autoAdd, percent) = ReadSettings();
        var pools = LoadPools();

        var added = 0;
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();

        foreach (var artist in artists)
        {
            var candidates = FindCandidates(artist, pools);
            MarkSubPools(candidates);

            using var transaction = connection.BeginTransaction();
            var seen = new HashSet<long>();
            foreach (var candidate in candidates)
            {
                seen.Add(candidate.Pool.Id);
                var share = 100.0 * candidate.ArtistPosts.Count / candidate.Pool.PostIds.Length;
                var state = StateOf(connection, transaction, artist.WatchId, candidate.Pool.Id);

                if (state == 0 && PoolAlreadyWatched(connection, transaction, candidate.Pool.Id))
                    state = 1;
                else if (state == 0 && autoAdd && candidate.IsRoot && share >= percent)
                {
                    AddPoolWatch(connection, transaction, candidate.Pool.Id, artist.Artist);
                    state = 1;
                    added++;
                }

                Upsert(connection, transaction, artist.WatchId, candidate, state);
            }

            // Suggestions that no longer apply (the artist's posts changed) go away; decisions are kept.
            using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText = "SELECT PoolId FROM PoolSuggestions WHERE ArtistWatchId = $id AND State = 0;";
                cleanup.Parameters.AddWithValue("$id", artist.WatchId);
                var stale = new List<long>();
                using (var reader = cleanup.ExecuteReader())
                    while (reader.Read())
                        if (!seen.Contains(reader.GetInt64(0))) stale.Add(reader.GetInt64(0));

                foreach (var poolId in stale)
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM PoolSuggestions WHERE ArtistWatchId = $id AND PoolId = $pool;";
                    delete.Parameters.AddWithValue("$id", artist.WatchId);
                    delete.Parameters.AddWithValue("$pool", poolId);
                    delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        if (added > 0) RequestMatch(connection);
        return added;
    }

    // ---------------------------------------------------------------- finding pools

    private static List<Candidate> FindCandidates(ArtistMatches artist, List<Pool> pools)
    {
        var posts = artist.PostIds.ToHashSet();
        var candidates = new List<Candidate>();
        foreach (var pool in pools)
        {
            HashSet<long>? inPool = null;
            foreach (var id in pool.PostIds)
                if (posts.Contains(id)) (inPool ??= []).Add(id);

            if (inPool is { Count: >= MinSharedPosts })
                candidates.Add(new Candidate(pool, inPool));
        }
        return candidates;
    }

    private static void MarkSubPools(List<Candidate> candidates)
    {
        var bySize = candidates.OrderByDescending(c => c.ArtistPosts.Count).ToList();
        foreach (var child in candidates)
        {
            foreach (var other in bySize)
            {
                if (other.ArtistPosts.Count <= child.ArtistPosts.Count) break;

                var shared = child.ArtistPosts.Count(other.ArtistPosts.Contains);
                if (shared >= MinSharedPosts && (double)shared / child.ArtistPosts.Count >= SubPoolThreshold)
                {
                    child.IsRoot = false;
                    break;
                }
            }
        }
    }

    private List<Pool> LoadPools()
    {
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Category, PostIds FROM Pools WHERE PostCount >= $min;";
        cmd.Parameters.AddWithValue("$min", MinSharedPosts);
        using var reader = cmd.ExecuteReader();

        var pools = new List<Pool>();
        while (reader.Read())
        {
            var ids = reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(i => long.TryParse(i, out var id) ? id : 0).Where(i => i > 0).ToArray();
            if (ids.Length >= MinSharedPosts) pools.Add(new Pool(reader.GetInt64(0), reader.GetString(1), ids));
        }
        return pools;
    }

    // ---------------------------------------------------------------- app database

    private (bool AutoAdd, int Percent) ReadSettings()
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        string? Get(string key)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }

        var enabled = Get(WorkerSettingsKeys.PoolsAutoAddEnabled) != "false";
        var percent = int.TryParse(Get(WorkerSettingsKeys.PoolsAutoAddPercent), out var p) ? Math.Clamp(p, 1, 100) : DefaultAutoAddPercent;
        return (enabled, percent);
    }

    private static int StateOf(SqliteConnection connection, SqliteTransaction transaction, long watchId, long poolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT State FROM PoolSuggestions WHERE ArtistWatchId = $id AND PoolId = $pool;";
        cmd.Parameters.AddWithValue("$id", watchId);
        cmd.Parameters.AddWithValue("$pool", poolId);
        return cmd.ExecuteScalar() is long state ? (int)state : 0;
    }

    private static bool PoolAlreadyWatched(SqliteConnection connection, SqliteTransaction transaction, long poolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM WatchedItems WHERE ItemType = $pool AND SourceValue = $id;";
        cmd.Parameters.AddWithValue("$pool", (int)WatchedItemType.Pool);
        cmd.Parameters.AddWithValue("$id", poolId.ToString());
        return cmd.ExecuteScalar() is not null;
    }

    private static void AddPoolWatch(SqliteConnection connection, SqliteTransaction transaction, long poolId, string artist)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO WatchedItems (ItemType, SourceValue, DisplayName, Enabled, AutoDownload, Origin)
            VALUES ($type, $id, $name, 1, 1, $origin);
            """;
        cmd.Parameters.AddWithValue("$type", (int)WatchedItemType.Pool);
        cmd.Parameters.AddWithValue("$id", poolId.ToString());
        cmd.Parameters.AddWithValue("$name", $"Pool #{poolId}");
        cmd.Parameters.AddWithValue("$origin", $"auto:{artist}");
        cmd.ExecuteNonQuery();
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, long watchId, Candidate candidate, int state)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO PoolSuggestions (ArtistWatchId, PoolId, ArtistPosts, PoolPosts, IsRoot, State)
            VALUES ($watch, $pool, $artistPosts, $poolPosts, $root, $state)
            ON CONFLICT(ArtistWatchId, PoolId) DO UPDATE SET
                ArtistPosts = $artistPosts, PoolPosts = $poolPosts, IsRoot = $root, State = $state;
            """;
        cmd.Parameters.AddWithValue("$watch", watchId);
        cmd.Parameters.AddWithValue("$pool", candidate.Pool.Id);
        cmd.Parameters.AddWithValue("$artistPosts", candidate.ArtistPosts.Count);
        cmd.Parameters.AddWithValue("$poolPosts", candidate.Pool.PostIds.Length);
        cmd.Parameters.AddWithValue("$root", candidate.IsRoot ? 1 : 0);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.ExecuteNonQuery();
    }

    private static void RequestMatch(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ('Worker.MatchRequested', 'true')
            ON CONFLICT(Key) DO UPDATE SET Value = 'true';
            """;
        cmd.ExecuteNonQuery();
    }
}
