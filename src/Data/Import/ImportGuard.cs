namespace SpankBank.Data.Import;

/// <summary>Refuses a posts import that would shrink the catalog far more than a daily export ever does (truncated or wrong file).</summary>
public static class ImportGuard
{
    /// <summary>Largest allowed drop in the post count, as a fraction of the existing catalog.</summary>
    public const double MaxShrinkFraction = 0.10;

    /// <summary>Below this many existing posts the catalog is new or a dev seed, and any import is fine.</summary>
    public const long MinExistingToGuard = 1000;

    /// <returns>The reason to refuse, or null when the import may replace the catalog.</returns>
    public static string? Check(long existing, long incoming, bool forced)
    {
        if (forced || existing < MinExistingToGuard) return null;
        var floor = (long)(existing * (1 - MaxShrinkFraction));
        if (incoming >= floor) return null;
        return $"Refused: the export has {incoming:N0} posts but the catalog has {existing:N0} "
            + $"(more than {MaxShrinkFraction:P0} fewer). Set {ImportSettingsKeys.ForceNextPostsImport} to true to allow it once.";
    }
}
