using System.Reflection;
using System.Text;
using TrPuzzle.Core;
using TrPuzzle.Engine;
using TrPuzzle.NativeBridge;

Run("ApprovedPuzzle has no public constructor", () =>
{
    Equal(0, typeof(ApprovedPuzzle).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length);
});

Run("Native candidate is independently verified", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-1");
    var range = new WorkRange(puzzle.KeyRangeStart, puzzle.KeyRangeEnd);
    using var accepted = NativeSearchCoordinator.VerifyNativeCandidate(puzzle, range, UInt256.Parse("1"), 1);
    True(accepted.Found);
    Throws<InvalidDataException>(() => NativeSearchCoordinator.VerifyNativeCandidate(puzzle, range, UInt256.Parse("2"), 2));
});

Run("Result vault is encrypted and refuses overwrite", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-1");
    using var result = new CpuReferenceSearcher().Search(puzzle, new WorkRange(puzzle.KeyRangeStart, puzzle.KeyRangeEnd));
    var directory = Path.Combine(Path.GetTempPath(), $"trpuzzle-tests-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "result.vault");
    try
    {
        EncryptedResultVault.WriteNew(path, puzzle, result, "correct horse battery staple");
        var bytes = File.ReadAllBytes(path);
        True(bytes.AsSpan(0, 8).SequenceEqual("TRPVLT01"u8));
        True(!Encoding.ASCII.GetString(bytes).Contains(puzzle.KeyRangeStart.ToString(), StringComparison.OrdinalIgnoreCase));
        Throws<IOException>(() => EncryptedResultVault.WriteNew(path, puzzle, result, "correct horse battery staple"));
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
});

Run("SQLite checkpoint resumes an expired lease atomically", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-1");
    var directory = Path.Combine(Path.GetTempPath(), $"trpuzzle-checkpoint-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "checkpoint.db");
    var start = DateTimeOffset.Parse("2026-09-20T10:00:00Z");
    try
    {
        var firstStore = new SqliteCheckpointStore(path);
        firstStore.Initialize();
        var sessionId = firstStore.CreateOrOpenSession(puzzle, 2, start);
        var firstLease = firstStore.TryLeaseNext(puzzle, sessionId, "worker-a", start, TimeSpan.FromMinutes(1));
        True(firstLease is not null);
        firstLease!.Package.Range.EnsureWithin(puzzle);
        True(firstLease.Package.Range.Start.TryIncrement(out var nextKey));
        firstLease = firstStore.SaveCheckpoint(puzzle, firstLease, nextKey, 1, 100, start.AddSeconds(1), TimeSpan.FromMinutes(1));

        var recoveredStore = new SqliteCheckpointStore(path);
        recoveredStore.Initialize();
        var recovered = recoveredStore.TryLeaseNext(puzzle, sessionId, "worker-b", start.AddMinutes(2), TimeSpan.FromMinutes(1));
        True(recovered is not null);
        Equal(firstLease.Package.Sequence, recovered!.Package.Sequence);
        Equal(firstLease.NextKey, recovered.NextKey);
        Equal(1UL, recovered.CheckedKeys);

        recoveredStore.CompletePackage(puzzle, recovered, 2, 100, start.AddMinutes(2).AddSeconds(1));
        var next = recoveredStore.TryLeaseNext(puzzle, sessionId, "worker-b", start.AddMinutes(2).AddSeconds(2), TimeSpan.FromMinutes(1));
        True(next is not null);
        next!.Package.Range.EnsureWithin(puzzle);
        var differentPuzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-16");
        Throws<InvalidDataException>(() => recoveredStore.GetSnapshot(differentPuzzle, sessionId));
        Throws<InvalidOperationException>(() => firstStore.SaveCheckpoint(puzzle, firstLease, firstLease.Package.Range.Start, 0, 100, start.AddMinutes(2), TimeSpan.FromMinutes(1)));
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
});

Run("CPU supervisor scans an approved public puzzle and stops on verification", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("btc-puzzle-1");
    var directory = Path.Combine(Path.GetTempPath(), $"trpuzzle-supervisor-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "checkpoint.db");
    try
    {
        var store = new SqliteCheckpointStore(path);
        store.Initialize();
        var sessionId = store.CreateOrOpenSession(puzzle, 1, DateTimeOffset.UtcNow);
        var found = false;
        var supervisor = new CpuWorkerSupervisor(store, new CpuWorkerSupervisorOptions
        {
            KeysPerPackage = 1,
            CheckpointEveryKeys = 1,
            LeaseDuration = TimeSpan.FromMinutes(1)
        });
        var snapshot = supervisor.Run(puzzle, sessionId, "cpu-test", result =>
        {
            found = result.Found;
            Equal(1UL, result.CheckedKeys);
        });
        True(found);
        True(snapshot.State == SearchSessionState.Found);
        Equal(1L, snapshot.CompletedPackages);
        Equal(0L, snapshot.LeasedPackages);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
});

Run("Supervisor honors pause and resumes the approved session", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("btc-puzzle-1");
    var directory = Path.Combine(Path.GetTempPath(), $"trpuzzle-pause-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "checkpoint.db");
    try
    {
        var store = new SqliteCheckpointStore(path);
        store.Initialize();
        var now = DateTimeOffset.UtcNow;
        var sessionId = store.CreateOrOpenSession(puzzle, 1, now);
        store.SetPaused(puzzle, sessionId, paused: true, now.AddSeconds(1));
        var supervisor = new CpuWorkerSupervisor(store, new CpuWorkerSupervisorOptions { KeysPerPackage = 1, CheckpointEveryKeys = 1 });
        var paused = supervisor.Run(puzzle, sessionId, "pause-test");
        True(paused.State == SearchSessionState.Paused);
        store.SetPaused(puzzle, sessionId, paused: false, now.AddSeconds(2));
        var resumed = supervisor.Run(puzzle, sessionId, "pause-test", result => True(result.Found));
        True(resumed.State == SearchSessionState.Found);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
});

Run("Completed session can start a deterministic verification epoch", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-1");
    var directory = Path.Combine(Path.GetTempPath(), $"trpuzzle-epoch-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "checkpoint.db");
    try
    {
        var store = new SqliteCheckpointStore(path);
        store.Initialize();
        var now = DateTimeOffset.UtcNow;
        var sessionId = store.CreateOrOpenSession(puzzle, 2, now);
        var sequence = 0;
        while (true)
        {
            var lease = store.TryLeaseNext(puzzle, sessionId, "epoch-test", now.AddSeconds(sequence), TimeSpan.FromMinutes(1));
            if (lease is null) break;
            var count = checked((ulong)(lease.Package.Range.End.ToBigInteger() - lease.Package.Range.Start.ToBigInteger() + System.Numerics.BigInteger.One));
            store.CompletePackage(puzzle, lease, count, 100, now.AddSeconds(sequence + 1));
            sequence++;
        }

        var completed = store.GetSnapshot(puzzle, sessionId);
        True(completed.State == SearchSessionState.Completed);
        Equal(0L, completed.Epoch);

        var nextEpoch = store.StartNewEpoch(puzzle, sessionId, now.AddMinutes(1));
        True(nextEpoch.State == SearchSessionState.Running);
        Equal(1L, nextEpoch.Epoch);
        var leaseInNewEpoch = store.TryLeaseNext(puzzle, sessionId, "epoch-test", now.AddMinutes(1).AddSeconds(1), TimeSpan.FromMinutes(1));
        True(leaseInNewEpoch is not null);
        leaseInNewEpoch!.Package.Range.EnsureWithin(puzzle);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
});

Console.WriteLine("TrPuzzle.Integration.Tests: all checks passed.");

static void Run(string name, Action test)
{
    try { test(); }
    catch (Exception exception) { throw new InvalidOperationException($"Test failed: {name}", exception); }
}

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Expected condition to be true.");
}

static void Equal<T>(T expected, T actual) where T : IEquatable<T>
{
    if (!expected.Equals(actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
