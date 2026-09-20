namespace TrPuzzle.Core;

public sealed record WorkPackage
{
    public WorkPackage(string puzzleId, long sequence, WorkRange range)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(puzzleId);
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));

        PuzzleId = puzzleId;
        Sequence = sequence;
        Range = range;
    }

    public string PuzzleId { get; }
    public long Sequence { get; }
    public WorkRange Range { get; }
}
