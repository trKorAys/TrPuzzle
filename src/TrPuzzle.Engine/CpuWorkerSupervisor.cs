using TrPuzzle.Core;

namespace TrPuzzle.Engine;

public sealed record CpuWorkerSupervisorOptions
{
    public ulong KeysPerPackage { get; init; } = 65_536;
    public ulong CheckpointEveryKeys { get; init; } = 4_096;
    public int MaxHexRun { get; init; }
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public Action<ScanProgress>? Progress { get; init; }
}

public sealed class CpuWorkerSupervisor
{
    private readonly ICheckpointStore _store;
    private readonly CpuPackageWorker _worker;
    private readonly CpuWorkerSupervisorOptions _options;

    public CpuWorkerSupervisor(
        ICheckpointStore store,
        CpuWorkerSupervisorOptions? options = null,
        CpuPackageWorker? worker = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new CpuWorkerSupervisorOptions();
        _worker = worker ?? new CpuPackageWorker();
        if (_options.KeysPerPackage == 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.CheckpointEveryKeys == 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.MaxHexRun is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(options));
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
            var sessionStopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                var lease = _store.TryLeaseNext(puzzle, sessionId, workerId, now, _options.LeaseDuration);
                if (lease is null)
                {
                    return _store.GetSnapshot(puzzle, sessionId);
                }

                try
                {
                    var packageStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    using var result = _worker.Scan(
                        puzzle,
                        lease,
                        _options.CheckpointEveryKeys,
                        _options.MaxHexRun,
                        (nextKey, checkedKeys) =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var packageSpeed = GetKeysPerSecond(checkedKeys, packageStopwatch.Elapsed);
                            _store.SaveCheckpoint(
                                puzzle,
                                lease,
                                nextKey,
                                checkedKeys,
                                packageSpeed,
                                DateTimeOffset.UtcNow,
                                _options.LeaseDuration);
                            ReportProgress(puzzle, lease, nextKey, checkedKeys, workerId, sessionStopwatch.Elapsed);
                        },
                        cancellationToken);

                    if (result.Found)
                    {
                        onVerifiedCandidate?.Invoke(result);
                        _store.MarkFound(
                            puzzle,
                            lease,
                            result.CheckedKeys,
                            GetKeysPerSecond(result.CheckedKeys, packageStopwatch.Elapsed),
                            DateTimeOffset.UtcNow);
                        return _store.GetSnapshot(puzzle, sessionId);
                    }

                    _store.CompletePackage(
                        puzzle,
                        lease,
                        result.CheckedKeys,
                        GetKeysPerSecond(result.CheckedKeys, packageStopwatch.Elapsed),
                        DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException)
                {
                    TryRelease(puzzle, lease, workerId, "cancelled-or-paused");
                    throw;
                }
                catch
                {
                    TryRelease(puzzle, lease, workerId, "worker-error");
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

    private static double GetKeysPerSecond(ulong checkedKeys, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return checkedKeys / seconds;
    }

    private void TryRelease(ApprovedPuzzle puzzle, WorkPackageLease lease, string reason, string workerId)
    {
        try
        {
            _store.ReleaseLease(puzzle, lease, reason, DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException)
        {
            // The lease may already have been reclaimed during cancellation.
        }
    }
}
