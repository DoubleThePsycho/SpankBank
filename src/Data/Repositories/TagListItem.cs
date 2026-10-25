using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record TagListItem(
    long Id,
    string Name,
    string Aliases,
    TagCategory Category,
    int PostCount
);

public sealed class TagListFilter
{
    public string? Name { get; set; }

    public TagCategory? Category { get; set; }
}

public enum TagOrderField
{
    PostCount,
    Name,
}
