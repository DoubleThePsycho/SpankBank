namespace SpankBank.Core.Models
{
    public sealed class Tag
    {
        public long        Id          { get; init; }
        public string      Name        { get; init; } = string.Empty;
        public TagCategory Category    { get; init; }
        public int         PostCount   { get; set; }
    }
}
