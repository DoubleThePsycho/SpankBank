using SpankBank.Core.Models;

namespace SpankBank.Core.Search;

public static class NumericSyntaxParser
{
    public static NumericFilter? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();

        // Range: 100..500
        int dotDot = s.IndexOf("..", StringComparison.Ordinal);
        if (dotDot >= 0)
        {
            var left  = s[..dotDot].Trim();
            var right = s[(dotDot + 2)..].Trim();
            if (long.TryParse(left, out var lo) && long.TryParse(right, out var hi))
            {
                if (lo > hi) (lo, hi) = (hi, lo);
                return new NumericFilter { Kind = NumericFilterKind.Range, Value = lo, High = hi };
            }
            return null;
        }

        // List: 100,121,144
        if (s.Contains(','))
        {
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var items = new List<long>(parts.Length);
            foreach (var p in parts)
            {
                if (!long.TryParse(p, out var n)) return null;
                items.Add(n);
            }
            if (items.Count == 0) return null;
            return new NumericFilter { Kind = NumericFilterKind.List, Items = items };
        }

        // >=, <= before > and < to avoid mis-parsing
        if (s.StartsWith(">=", StringComparison.Ordinal) && long.TryParse(s[2..].Trim(), out var geVal))
            return new NumericFilter { Kind = NumericFilterKind.GreaterOrEqual, Value = geVal };
        if (s.StartsWith("<=", StringComparison.Ordinal) && long.TryParse(s[2..].Trim(), out var leVal))
            return new NumericFilter { Kind = NumericFilterKind.LessOrEqual, Value = leVal };
        if (s.StartsWith('>') && long.TryParse(s[1..].Trim(), out var gtVal))
            return new NumericFilter { Kind = NumericFilterKind.GreaterThan, Value = gtVal };
        if (s.StartsWith('<') && long.TryParse(s[1..].Trim(), out var ltVal))
            return new NumericFilter { Kind = NumericFilterKind.LessThan, Value = ltVal };

        // Exact
        if (long.TryParse(s, out var exact))
            return new NumericFilter { Kind = NumericFilterKind.Exact, Value = exact };

        return null;
    }
}
