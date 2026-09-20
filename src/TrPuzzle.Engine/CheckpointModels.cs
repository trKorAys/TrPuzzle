using TrPuzzle.Core;
using System.Numerics;

namespace TrPuzzle.Engine;

public enum SearchSessionState
{
    Running,
    Paused,
    Completed,
    Found,
    Stopped
}

public sealed record WorkPackageLease(
    long SessionId,
    WorkPackage Package,
    UInt256 NextKey,
    string WorkerId,
    DateTimeOffset LeaseExpiresAt,
    ulong CheckedKeys);

public sealed record ScanProgress(
    string PuzzleId,
    string WorkerId,
    WorkRange ActiveRange,
    UInt256 NextKey,
    ulong CheckedInRange,
    BigInteger RangeSize,
    ulong TotalChecked,
    long CompletedPackages,
    BigInteger TotalPackages,
    double KeysPerSecond,
    TimeSpan Elapsed);

public sealed record SearchSessionSnapshot(
    long SessionId,
    string PuzzleId,
    long Epoch,
    SearchSessionState State,
    long PendingPackages,
    long LeasedPackages,
    long CompletedPackages,
    ulong CheckedKeys,
    UInt256? NextUnassignedKey);

public interface ICheckpointStore
{
    void Initialize();
    long CreateOrOpenSession(ApprovedPuzzle puzzle, ulong keysPerPackage, DateTimeOffset now);
    long? GetSessionId(ApprovedPuzzle puzzle);
    WorkPackageLease? TryLeaseNext(
        ApprovedPuzzle puzzle,
        long sessionId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration);
    WorkPackageLease SaveCheckpoint(
        ApprovedPuzzle puzzle,
        WorkPackageLease lease,
        UInt256 nextKey,
        ulong checkedKeys,
        double keysPerSecond,
        DateTimeOffset now,
        TimeSpan leaseDuration);
    void CompletePackage(
        ApprovedPuzzle puzzle,
        WorkPackageLease lease,
        ulong checkedKeys,
        double keysPerSecond,
        DateTimeOffset now);
    void ReleaseLease(ApprovedPuzzle puzzle, WorkPackageLease lease, string reason, DateTimeOffset now);
    void SetPaused(ApprovedPuzzle puzzle, long sessionId, bool paused, DateTimeOffset now);
    SearchSessionSnapshot StartNewEpoch(ApprovedPuzzle puzzle, long sessionId, DateTimeOffset now);
    void MarkFound(ApprovedPuzzle puzzle, WorkPackageLease lease, ulong checkedKeys, double keysPerSecond, DateTimeOffset now);
    SearchSessionSnapshot GetSnapshot(ApprovedPuzzle puzzle, long sessionId);
}
