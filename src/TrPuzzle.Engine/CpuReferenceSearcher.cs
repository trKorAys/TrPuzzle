using TrPuzzle.Core;

namespace TrPuzzle.Engine;

public sealed class CpuReferenceSearcher
{
    public SearchResult Search(ApprovedPuzzle puzzle, WorkRange range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        range.EnsureWithin(puzzle);

        var candidate = range.Start;
        ulong checkedKeys = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedKeys = checked(checkedKeys + 1);
            if (CandidateVerifier.Verify(puzzle, candidate))
            {
                return new SearchResult(checkedKeys, candidate.ToBigEndianBytes());
            }

            if (candidate == range.End)
            {
                return new SearchResult(checkedKeys, privateKey: null);
            }

            if (!candidate.TryIncrement(out candidate))
            {
                throw new InvalidOperationException("UInt256 range overflowed before reaching its declared end.");
            }
        }
    }
}
