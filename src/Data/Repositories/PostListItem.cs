using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record PostListItem(
    long Id,
    PostRating Rating,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Score,
    string? FileExt,
    string? Md5,
    int FavCount
);
