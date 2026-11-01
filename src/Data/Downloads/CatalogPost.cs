namespace SpankBank.Data.Downloads;

/// <summary>A post as the imported catalog (server.sqlite) knows it.</summary>
public sealed record CatalogPost(
    long Id,
    string Md5,
    string Ext,
    bool IsDeleted,
    int Rating,
    int Score,
    int? Width,
    int? Height,
    long? FileSize,
    string CreatedAt,
    string UpdatedAt,
    string TagString,
    string? Description,
    double? Duration,
    long? ParentId,
    string? Source = null);
