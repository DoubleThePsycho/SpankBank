using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record PoolDetail(
    long Id,
    string Name,
    string DisplayName,
    PoolType PoolType,
    int PostCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt
);

public sealed record PoolPostListItem(
    long Id,
    PostRating Rating,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Score,
    string? FileExt,
    int Position
);

public sealed record PoolListItem(
    long Id,
    string Name,
    string DisplayName,
    PoolType PoolType,
    int PostCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt
);

public sealed class PoolListFilter
{
    public long? Id { get; set; }

    public string? Name { get; set; }

    public PoolType? PoolType { get; set; }
}

public enum PoolOrderField
{
    Id,
    Name,
    PostCount,
    CreatedAt,
    UpdatedAt,
}
