namespace SpankBank.Core.Models
{
    public sealed class PostFile
    {
        public int    Width     { get; init; }
        public int    Height    { get; init; }
        public long   Size      { get; init; }
        public string Ext       { get; init; } = string.Empty;
        public string Md5       { get; init; } = string.Empty;
        public string? Url      { get; init; }
        public string? LocalPath { get; set; }
    }
}
