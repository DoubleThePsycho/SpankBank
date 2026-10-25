using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record PostDetail(
    long Id,
    PostRating Rating,
    int Score,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? SavedAt,
    int? FileWidth,
    int? FileHeight,
    long? FileSize,
    string? FileExt,
    string? FileMd5,
    string? FileUrl,
    string? FileLocalPath,
    long? ParentId,
    bool Deleted,
    IReadOnlyList<PostDetailTag> Tags,
    IReadOnlyList<PostDetailPool> Pools,
    IReadOnlyList<long> ChildPostIds
);

public sealed record PostDetailTag(
    string Name,
    string DisplayName,
    TagCategory Category,
    int PostCount
);

public sealed record PostDetailPool(
    long Id,
    string Name,
    string DisplayName,
    PoolType PoolType,
    int PostCount,
    int Position
);
