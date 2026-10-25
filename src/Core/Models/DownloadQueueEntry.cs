namespace SpankBank.Core.Models
{
    public sealed class DownloadQueueEntry
    {
        public long              Id              { get; init; }
        public DownloadEntryType EntryType       { get; init; }
        public string            SourceValue     { get; init; } = string.Empty;
        public string            DisplayName     { get; set; }  = string.Empty;
        public DownloadStatus    Status          { get; set; }
        public bool              WatchEnabled    { get; set; }
        public DateTimeOffset    CreatedAt       { get; init; }
        public DateTimeOffset    UpdatedAt       { get; set; }
        public DateTimeOffset?   LastAttemptedAt { get; set; }
        public string?           ErrorMessage    { get; set; }
        public int               AttemptCount    { get; set; }
        public int               MatchedPosts    { get; set; }
        public int               DownloadedPosts { get; set; }
        public int               FailedPosts     { get; set; }
        public string?           Destination     { get; set; }
    }
}
