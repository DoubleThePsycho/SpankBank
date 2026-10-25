namespace SpankBank.Core.Models
{
    public sealed class PostTags
    {
        public string[] General   { get; init; } = [];
        public string[] Artist    { get; init; } = [];
        public string[] Copyright { get; init; } = [];
        public string[] Character { get; init; } = [];
        public string[] Species   { get; init; } = [];
        public string[] Invalid   { get; init; } = [];
        public string[] Meta      { get; init; } = [];
        public string[] Lore      { get; init; } = [];
    }
}
