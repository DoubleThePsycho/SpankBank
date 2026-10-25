namespace SpankBank.Core.Models
{
    public sealed class Pool
    {
        public long            Id          { get; init; }
        public string          Name        { get; init; } = string.Empty;
        public string          DisplayName { get; init; } = string.Empty;
        public PoolType        PoolType    { get; init; }
        public int             PostCount   { get; set; }
        public DateTimeOffset? CreatedAt   { get; init; }
        public DateTimeOffset? UpdatedAt   { get; set; }
        public long[]          PostIds     { get; set; } = [];
    }
}
