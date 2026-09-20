using TrPuzzle.Core;
using TrPuzzle.Engine;
using System.Diagnostics;

namespace TrPuzzle.NativeBridge;

public sealed class CudaWorkerSupervisor
{
    private readonly ICheckpointStore _store;
    private readonly NativeSearchCoordinator _coordinator;
    private readonly CpuWorkerSupervisorOptions _options;

    public CudaWorkerSupervisor(
        ICheckpointStore store,
        NativeCudaRuntime runtime,
        uint ordinal,
        CpuWorkerSupervisorOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _coordinator = new NativeSearchCoordinator(runtime ?? throw new ArgumentNullException(nameof(runtime)), ordinal);
        _options = options ?? new CpuWorkerSupervisorOptions();
        if (_options.KeysPerPackage == 0 || _options.KeysPerPackage > 1_048_576) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.CheckpointEveryKeys == 0 || _options.CheckpointEveryKeys > 1_048_576) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.LeaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public SearchSessionSnapshot Run(
        ApprovedPuzzle puzzle,
        long sessionId,
        string workerId,
        Action<SearchResult>? onVerifiedCandidate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        _store.Initialize();

        try
        {
            var sessionStopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lease = _store.TryLeaseNext(puzzle, sessionId, workerId, DateTimeOffset.UtcNow, _options.LeaseDuration);
                if (lease is null) return _store.GetSnapshot(puzzle, sessionId);
                try
                {
                    var packageStopwatch = Stopwatch.StartNew();
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var batchEnd = GetBatchEnd(lease.NextKey, lease.Package.Range.End, _options.CheckpointEveryKeys);

                        // Publish the active batch before entering the synchronous
                        // native call. Large CUDA batches can legitimately take
                        // time; the dashboard must not look idle while the GPU is
                        // working on the first checkpoint.
                        ReportProgress(puzzle, lease, lease.NextKey, lease.CheckedKeys, workerId, sessionStopwatch.Elapsed);

                        using var result = _coordinator.Search(puzzle, new WorkRange(lease.NextKey, batchEnd), _options.MaxHexRun, cancellationToken);
                        var checkedKeys = checked(lease.CheckedKeys + result.CheckedKeys);
                        var keysPerSecond = GetKeysPerSecond(checkedKeys, packageStopwatch.Elapsed);

                        if (result.Found)
                        {
                            onVerifiedCandidate?.Invoke(result);
                            _store.MarkFound(puzzle, lease, checkedKeys, keysPerSecond, DateTimeOffset.UtcNow);
                            return _store.GetSnapshot(puzzle, sessionId);
                        }

                        if (batchEnd == lease.Package.Range.End)
                        {
                            _store.CompletePackage(puzzle, lease, checkedKeys, keysPerSecond, DateTimeOffset.UtcNow);
                            break;
                        }

                        if (!batchEnd.TryIncrement(out var nextKey))
                        {
                            throw new InvalidOperationException("CUDA package worker overflowed its assigned range.");
                        }

                        lease = _store.SaveCheckpoint(
                            puzzle,
                            lease,
                            nextKey,
                            checkedKeys,
                            keysPerSecond,
                            DateTimeOffset.UtcNow,
                            _options.LeaseDuration);
                        ReportProgress(puzzle, lease, nextKey, checkedKeys, workerId, sessionStopwatch.Elapsed);
                    }

                }
                catch (OperationCanceledException)
                {
                    TryRelease(puzzle, lease, "cuda-cancelled");
                    throw;
                }
                catch
                {
                    TryRelease(puzzle, lease, "cuda-error");
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return _store.GetSnapshot(puzzle, sessionId);
        }
    }

    private void ReportProgress(ApprovedPuzzle puzzle, WorkPackageLease lease, UInt256 nextKey, ulong checkedKeys, string workerId, TimeSpan elapsed)
    {
        if (_options.Progress is null) return;
        var snapshot = _store.GetSnapshot(puzzle, lease.SessionId);
        var rangeSize = lease.Package.Range.End.ToBigInteger() - lease.Package.Range.Start.ToBigInteger() + System.Numerics.BigInteger.One;
        _options.Progress(new ScanProgress(
            puzzle.Id, workerId, lease.Package.Range, nextKey, checkedKeys, rangeSize,
            snapshot.CheckedKeys, snapshot.CompletedPackages,
            RangePlanner.GetChunkCount(puzzle, _options.KeysPerPackage),
            snapshot.CheckedKeys / Math.Max(elapsed.TotalSeconds, 0.001), elapsed));
    }

    private void TryRelease(ApprovedPuzzle puzzle, WorkPackageLease lease, string reason)
    {
        try { _store.ReleaseLease(puzzle, lease, reason, DateTimeOffset.UtcNow); }
        catch (InvalidOperationException) { }
    }

    private static UInt256 GetBatchEnd(UInt256 start, UInt256 packageEnd, ulong batchSize)
    {
        if (!start.TryAdd(batchSize - 1, out var proposedEnd) || proposedEnd > packageEnd)
        {
            return packageEnd;
        }

        return proposedEnd;
    }

    private static double GetKeysPerSecond(ulong checkedKeys, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return checkedKeys / seconds;
    }
}
