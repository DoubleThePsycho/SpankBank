namespace SpankBank.Core.Models
{
    public sealed class WatchedItem
    {
        public long            Id               { get; init; }
        public WatchedItemType ItemType         { get; init; }
        public string          SourceValue      { get; init; } = string.Empty;
        public string          DisplayName      { get; set; }  = string.Empty;
        public bool            Enabled          { get; set; }
        public bool            AutoDownload     { get; set; }
        public DateTimeOffset? LastCheckedAt    { get; set; }
        public string?         LastResultSummary { get; set; }
        public string?         ErrorMessage     { get; set; }
    }
}
