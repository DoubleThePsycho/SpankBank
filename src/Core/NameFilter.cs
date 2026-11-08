using System.Text.RegularExpressions;

namespace SpankBank.Core;

/// <summary>
/// The name box of the list searches: case-insensitive, spaces and underscores are the same, plain text matches anywhere in the
/// name, and <c>*</c> is a wildcard (then the whole name must match, e.g. <c>fox*</c> = starts with fox).
/// </summary>
public static class NameFilter
{
    public static bool IsEmpty(string? pattern) => string.IsNullOrWhiteSpace(pattern);

    public static bool Matches(string? pattern, string? value)
    {
        if (IsEmpty(pattern)) return true;
        if (value is null) return false;

        var wanted = Normalize(pattern!);
        var name = Normalize(value);
        if (!wanted.Contains('*')) return name.Contains(wanted, StringComparison.Ordinal);

        var regex = "^" + string.Join(".*", wanted.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The pattern as an SQL LIKE pattern (use with <c>ESCAPE '\'</c> on <c>lower(replace(col, '_', ' '))</c>).</summary>
    public static string ToLike(string pattern)
    {
        var text = Normalize(pattern).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("*", "%");
        return pattern.Contains('*') ? text : "%" + text + "%";
    }

    private static string Normalize(string text) => text.Trim().Replace('_', ' ').ToLowerInvariant();
}
