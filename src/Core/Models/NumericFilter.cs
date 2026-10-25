namespace SpankBank.Core.Models;

public sealed class NumericFilter
{
    public NumericFilterKind Kind { get; init; }
    // Exact, comparison ops: Value is the operand.
    // Range: Value is the lower bound (inclusive), High is the upper bound (inclusive).
    // List: Items holds the values; Value and High are unused.
    public long Value { get; init; }
    public long High { get; init; }
    public IReadOnlyList<long>? Items { get; init; }
}
