namespace SpankBank.Core.Models;

public sealed class PostListFilter
{
    // Parsed from a single e621-style syntax input (e.g. >100, 100..500, 100,121,144).
    public NumericFilter? Id { get; set; }
    public NumericFilter? Score { get; set; }

    public bool IncludeSafe { get; set; } = true;
    public bool IncludeQuestionable { get; set; } = true;
    public bool IncludeExplicit { get; set; } = true;

    // Empty = any; non-empty = only matching extensions.
    public HashSet<string> FileExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PoolMembershipFilter PoolMembership { get; set; } = PoolMembershipFilter.Anything;
    public PoolTypeFilter PoolType { get; set; } = PoolTypeFilter.Any;

    // Parsed from single syntax inputs. Null = no filter.
    public DateFilter? DateUploaded { get; set; }
    public DateFilter? DateUpdated { get; set; }
    public DateFilter? DateDownloaded { get; set; }
}
