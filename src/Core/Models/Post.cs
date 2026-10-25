namespace SpankBank.Core.Models
{
    public sealed class Post
    {
        public long            Id            { get; init; }
        public DateTimeOffset  CreatedAt     { get; init; }
        public DateTimeOffset  UpdatedAt     { get; set; }
        public DateTimeOffset? FetchedAt     { get; set; }
        public DateTimeOffset? SavedAt       { get; set; }

        public PostFile?         File          { get; set; }
        public PostTags?         Tags          { get; set; }
        public PostRelationships? Relationships { get; set; }

        public PostRating Rating  { get; set; }
        public int        Score   { get; set; }
        public long[]     Pools   { get; set; } = [];
        public bool       Deleted { get; set; }
    }
}
