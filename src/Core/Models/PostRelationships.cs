namespace SpankBank.Core.Models
{
    public sealed class PostRelationships
    {
        public long?  ParentId  { get; init; }
        public long[] ChildIds  { get; init; } = [];
    }
}
