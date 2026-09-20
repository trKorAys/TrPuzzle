using TrPuzzle.Core;

namespace TrPuzzle.Engine;

public sealed class CpuPackageWorker
{
    public SearchResult Scan(
        ApprovedPuzzle puzzle,
        WorkPackageLease lease,
        ulong checkpointEveryKeys,
        int maxHexRun,
        Action<UInt256, ulong> checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpointEveryKeys == 0) throw new ArgumentOutOfRangeException(nameof(checkpointEveryKeys));
        if (maxHexRun is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(maxHexRun));
        lease.Package.Range.EnsureWithin(puzzle);

        var candidate = lease.NextKey;
        ulong checkedKeys = lease.CheckedKeys;
        ulong localKeys = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedKeys = checked(checkedKeys + 1);
            localKeys = checked(localKeys + 1);
            if (HexRunFilter.Allows(candidate, maxHexRun) && CandidateVerifier.Verify(puzzle, candidate))
            {
                return new SearchResult(checkedKeys, candidate.ToBigEndianBytes());
            }

            if (candidate == lease.Package.Range.End)
            {
                return new SearchResult(checkedKeys, privateKey: null);
            }

            if (!candidate.TryIncrement(out candidate))
            {
                throw new InvalidOperationException("CPU package worker overflowed its assigned range.");
            }

            if (localKeys % checkpointEveryKeys == 0)
            {
                checkpoint(candidate, checkedKeys);
            }
        }
    }
}
