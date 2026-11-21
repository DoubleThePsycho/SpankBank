using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpankBank.Data.Downloads;

/// <summary>What a deletion reason means to the "skip inferior versions and low-effort edits" setting.</summary>
public enum DeletionKind
{
    /// <summary>Any other reason (or none known): the post is still worth recovering.</summary>
    Other,
    /// <summary>"Duplicate or inferior version of another post".</summary>
    Inferior,
    /// <summary>"Does not meet minimum quality standards (Trivial or low quality edit)".</summary>
    LowEffortEdit,
}

/// <summary>Reads e621's deletion reasons (post flags of type "deletion", from <c>/post_flags.json</c>).</summary>
public static partial class DeletionReasons
{
    public static DeletionKind Classify(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return DeletionKind.Other;
        if (InferiorVersion().IsMatch(reason)) return DeletionKind.Inferior;
        if (LowEffortEdit().IsMatch(reason)) return DeletionKind.LowEffortEdit;
        return DeletionKind.Other;
    }

    /// <summary>The value stored in <c>DeletionReasons.Kind</c>: null for <see cref="DeletionKind.Other"/>.</summary>
    public static string? StoredKind(DeletionKind kind) => kind switch
    {
        DeletionKind.Inferior => "inferior",
        DeletionKind.LowEffortEdit => "edit",
        _ => null,
    };

    /// <summary>
    /// The reason of each requested post's latest deletion flag (null when it has none), from a <c>post_flags.json</c> answer.
    /// Null when the answer is not a list of flags (an error object, an HTML page).
    /// </summary>
    public static IReadOnlyDictionary<long, string?>? Parse(string json, IEnumerable<long> requested)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var latest = new Dictionary<long, (long FlagId, string Reason)>();
            foreach (var flag in document.RootElement.EnumerateArray())
            {
                if (flag.ValueKind != JsonValueKind.Object
                    || !flag.TryGetProperty("post_id", out var post) || !post.TryGetInt64(out var postId)
                    || !flag.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var flagId)
                    || !flag.TryGetProperty("type", out var type) || type.GetString() != "deletion"
                    || !flag.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String) continue;

                if (!latest.TryGetValue(postId, out var known) || flagId > known.FlagId) latest[postId] = (flagId, reason.GetString()!);
            }

            return requested.Distinct().ToDictionary(id => id, id => latest.TryGetValue(id, out var found) ? found.Reason : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"inferior version", RegexOptions.IgnoreCase)]
    private static partial Regex InferiorVersion();

    [GeneratedRegex(@"trivial (?:or low[- ]quality )?edit|low[- ]effort edit", RegexOptions.IgnoreCase)]
    private static partial Regex LowEffortEdit();
}
