namespace TrPuzzle.Core;

public readonly record struct WorkRange
{
    public WorkRange(UInt256 start, UInt256 end)
    {
        if (start > end)
        {
            throw new ArgumentException("Work range start must not exceed its end.");
        }

        Start = start;
        End = end;
    }

    public UInt256 Start { get; }
    public UInt256 End { get; }

    public void EnsureWithin(ApprovedPuzzle puzzle)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        if (Start < puzzle.KeyRangeStart || End > puzzle.KeyRangeEnd)
        {
            throw new InvalidOperationException("Work range is outside the approved puzzle boundary.");
        }
    }
}
