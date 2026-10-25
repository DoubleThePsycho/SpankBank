using System.Globalization;
using System.Text.RegularExpressions;
using SpankBank.Core.Models;

namespace SpankBank.Core.Search;

public static class DateSyntaxParser
{
    // Parses e621-style date syntax into a normalized DateFilter (all UTC).
    // Supported: empty (null), >date, <date, >=date, <=date, date..date (normalized if reversed),
    // exact date (mapped to inclusive day range), N_days_ago, N_weeks_ago,
    // N_months_ago, N_years_ago, YYYY/MM or MM/YYYY.
    public static DateFilter? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();

        // Range: date..date
        int dotDot = s.IndexOf("..", StringComparison.Ordinal);
        if (dotDot >= 0)
        {
            var left  = s[..dotDot].Trim();
            var right = s[(dotDot + 2)..].Trim();
            var lo = ParseSingleDate(left);
            var hi = ParseSingleDate(right);
            if (lo.HasValue && hi.HasValue)
            {
                var low  = StartOfDay(lo.Value);
                var high = EndOfDay(hi.Value);
                if (low > high)
                {
                    low  = StartOfDay(hi.Value);
                    high = EndOfDay(lo.Value);
                }
                return new DateFilter { Kind = DateFilterKind.Range, Value = low, High = high };
            }
            return null;
        }

        // >= and <= before > and < to avoid short-circuit mis-parse
        if (s.StartsWith(">=", StringComparison.Ordinal))
        {
            var d = ParseSingleDate(s[2..].Trim());
            if (d.HasValue) return new DateFilter { Kind = DateFilterKind.GreaterOrEqual, Value = StartOfDay(d.Value) };
            return null;
        }
        if (s.StartsWith("<=", StringComparison.Ordinal))
        {
            var d = ParseSingleDate(s[2..].Trim());
            if (d.HasValue) return new DateFilter { Kind = DateFilterKind.LessOrEqual, Value = EndOfDay(d.Value) };
            return null;
        }
        if (s.StartsWith(">", StringComparison.Ordinal))
        {
            var d = ParseSingleDate(s[1..].Trim());
            if (d.HasValue) return new DateFilter { Kind = DateFilterKind.GreaterThan, Value = EndOfDay(d.Value) };
            return null;
        }
        if (s.StartsWith("<", StringComparison.Ordinal))
        {
            var d = ParseSingleDate(s[1..].Trim());
            if (d.HasValue) return new DateFilter { Kind = DateFilterKind.LessThan, Value = StartOfDay(d.Value) };
            return null;
        }

        // Relative: 5_days_ago
        var daysMatch = Regex.Match(s, @"^(\d+)_days_ago$", RegexOptions.IgnoreCase);
        if (daysMatch.Success)
        {
            int n = int.Parse(daysMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return DayRange(DateTimeOffset.UtcNow.AddDays(-n));
        }

        // Relative: 2_weeks_ago
        var weeksMatch = Regex.Match(s, @"^(\d+)_weeks_ago$", RegexOptions.IgnoreCase);
        if (weeksMatch.Success)
        {
            int n = int.Parse(weeksMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return DayRange(DateTimeOffset.UtcNow.AddDays(-n * 7));
        }

        // Relative: 2_months_ago
        var monthsMatch = Regex.Match(s, @"^(\d+)_months_ago$", RegexOptions.IgnoreCase);
        if (monthsMatch.Success)
        {
            int n = int.Parse(monthsMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return DayRange(DateTimeOffset.UtcNow.AddMonths(-n));
        }

        // Relative: 1_years_ago
        var yearsMatch = Regex.Match(s, @"^(\d+)_years_ago$", RegexOptions.IgnoreCase);
        if (yearsMatch.Success)
        {
            int n = int.Parse(yearsMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            return DayRange(DateTimeOffset.UtcNow.AddYears(-n));
        }

        // Month/year: 2024/01, 2024-01, 01/2024, 01-2024
        var monthFilter = TryParseMonthYear(s);
        if (monthFilter is not null) return monthFilter;

        // Exact single date
        var single = ParseSingleDate(s);
        if (single.HasValue) return DayRange(single.Value);

        return null;
    }

    private static DateFilter DayRange(DateTimeOffset d) =>
        new() { Kind = DateFilterKind.Range, Value = StartOfDay(d), High = EndOfDay(d) };

    private static DateTimeOffset StartOfDay(DateTimeOffset d) =>
        new(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset EndOfDay(DateTimeOffset d) =>
        new(d.Year, d.Month, d.Day, 23, 59, 59, 999, TimeSpan.Zero);

    private static DateTimeOffset? ParseSingleDate(string s)
    {
        var relative = ParseRelativeDate(s);
        if (relative.HasValue)
            return relative.Value;

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
            return dto.ToUniversalTime();
        return null;
    }

    private static DateTimeOffset? ParseRelativeDate(string s)
    {
        var match = Regex.Match(s, @"^(\d+)_(days|weeks|months|years)_ago$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        int n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "days" => DateTimeOffset.UtcNow.AddDays(-n),
            "weeks" => DateTimeOffset.UtcNow.AddDays(-n * 7),
            "months" => DateTimeOffset.UtcNow.AddMonths(-n),
            "years" => DateTimeOffset.UtcNow.AddYears(-n),
            _ => null,
        };
    }

    private static DateFilter? TryParseMonthYear(string s)
    {
        // YYYY/MM or YYYY-MM
        if (Regex.IsMatch(s, @"^\d{4}[/\-]\d{1,2}$"))
        {
            int sep = s.IndexOfAny(['/', '-']);
            if (int.TryParse(s[..sep], out int year)
                && int.TryParse(s[(sep + 1)..], out int month)
                && month is >= 1 and <= 12)
            {
                var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
                var end   = start.AddMonths(1).AddTicks(-1);
                return new DateFilter { Kind = DateFilterKind.Range, Value = start, High = end };
            }
        }
        // MM/YYYY or MM-YYYY
        if (Regex.IsMatch(s, @"^\d{1,2}[/\-]\d{4}$"))
        {
            int sep = s.IndexOfAny(['/', '-']);
            if (int.TryParse(s[..sep], out int month)
                && int.TryParse(s[(sep + 1)..], out int year)
                && month is >= 1 and <= 12)
            {
                var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
                var end   = start.AddMonths(1).AddTicks(-1);
                return new DateFilter { Kind = DateFilterKind.Range, Value = start, High = end };
            }
        }
        return null;
    }
}
