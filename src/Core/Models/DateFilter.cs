namespace SpankBank.Core.Models;

public sealed class DateFilter
{
    public DateFilterKind Kind { get; init; }
    // Comparison ops: Value is the operand (UTC).
    // Range: Value is the lower bound (inclusive), High is the upper bound (inclusive). Both UTC.
    public DateTimeOffset Value { get; init; }
    public DateTimeOffset High { get; init; }
}
