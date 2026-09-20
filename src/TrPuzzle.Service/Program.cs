using System.Globalization;
using System.Diagnostics;
using TrPuzzle.Core;
using TrPuzzle.Engine;
using TrPuzzle.NativeBridge;

return Run(args);

static int Run(string[] args)
{
    var catalog = ApprovedPuzzleCatalog.LoadEmbedded();
    if (args.Length == 1 && args[0] == "list")
    {
        foreach (var puzzle in catalog.All.OrderBy(static puzzle => puzzle.Id, StringComparer.Ordinal))
        {
            Console.WriteLine($"{puzzle.Id}\t{puzzle.Kind}\t{puzzle.Status}\t{puzzle.Name}");
        }

        return 0;
    }

    if (args.Length == 1 && args[0] == "self-test")
    {
        var puzzle = catalog.GetRequired("test-scalar-16");
        var searcher = new CpuReferenceSearcher();
        using var result = searcher.Search(puzzle, new WorkRange(puzzle.KeyRangeStart, puzzle.KeyRangeEnd));
        if (!result.Found)
        {
            Console.Error.WriteLine("CPU self-test failed: built-in candidate was not found.");
            return 2;
        }

        Console.WriteLine($"CPU self-test passed; checked={result.CheckedKeys}; candidate-fingerprint={result.CandidateFingerprint}");
        return 0;
    }

    if (args.Length > 0 && args[0] is "scan" or "status" or "pause" or "resume" or "rescan" or "vault-info" or "vault-reveal")
    {
        return RunSessionCommand(args, catalog);
    }

    if (args.Length > 0 && args[0] is "devices" or "gpu-self-test" or "gpu-benchmark")
    {
        return RunNativeCommand(args, catalog);
    }

    PrintUsage();
    return args.Length == 0 ? 0 : 1;
}

static int RunSessionCommand(string[] args, ApprovedPuzzleCatalog catalog)
{
    var command = args[0];
    Dictionary<string, string> options;
    try
    {
        options = ParseOptions(args[1..], "puzzle", "db", "chunk", "checkpoint", "vault", "worker", "native", "max-hex-run");
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine($"Operation rejected: {exception.Message}");
        return 1;
    }
    if (!options.TryGetValue("puzzle", out var puzzleId))
    {
        Console.Error.WriteLine("Missing required --puzzle <approved-id>.");
        return 1;
    }

    ApprovedPuzzle puzzle;
    try { puzzle = catalog.GetRequired(puzzleId); }
    catch (KeyNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }

    var databasePath = options.GetValueOrDefault("db") ?? Path.Combine("checkpoints", $"{puzzle.Id}.db");
    var store = new SqliteCheckpointStore(databasePath);
    store.Initialize();

    try
    {
        return command switch
        {
            "scan" => RunScan(options, puzzle, store),
            "status" => RunStatus(puzzle, store),
            "pause" => SetPaused(puzzle, store, paused: true),
            "resume" => SetPaused(puzzle, store, paused: false),
            "rescan" => StartNewEpoch(puzzle, store),
            "vault-info" or "vault-reveal" => RunVaultCommand(command, options, puzzle),
            _ => 1
        };
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or KeyNotFoundException or IOException)
    {
        Console.Error.WriteLine($"Operation rejected: {exception.Message}");
        return 2;
    }
}

static int RunNativeCommand(string[] args, ApprovedPuzzleCatalog catalog)
{
    Dictionary<string, string> options;
    try
    {
        options = ParseOptions(args[1..], "native", "device", "puzzle", "iterations");
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine($"Operation rejected: {exception.Message}");
        return 1;
    }

    try
    {
        using var runtime = new NativeCudaRuntime(options.GetValueOrDefault("native"));
        var devices = runtime.GetDevices();
        if (args[0] == "devices")
        {
            foreach (var device in devices)
            {
                Console.WriteLine($"cuda:{device.Ordinal}\t{device.Name}\t{device.TotalMemoryBytes / (1024 * 1024)} MiB\tcompute {device.ComputeMajor}.{device.ComputeMinor}");
            }

            return devices.Count == 0 ? 2 : 0;
        }

        var ordinal = ParseDeviceOrdinal(options);
        if (!devices.Any(device => device.Ordinal == ordinal))
        {
            throw new ArgumentOutOfRangeException("device", "Requested CUDA device is not available.");
        }

        if (args[0] == "gpu-benchmark")
        {
            var puzzleId = options.GetValueOrDefault("puzzle") ?? "test-scalar-16";
            var benchmarkPuzzle = catalog.GetRequired(puzzleId);
            var iterations = ParsePositiveInt(options, "iterations", 3, 1, 100);
            var count = benchmarkPuzzle.KeyRangeEnd.ToBigInteger() - benchmarkPuzzle.KeyRangeStart.ToBigInteger() + System.Numerics.BigInteger.One;
            if (count <= 0 || count > 1_048_576) throw new ArgumentOutOfRangeException("puzzle", "Benchmark range must contain at most 1048576 candidates.");

            var range = new WorkRange(benchmarkPuzzle.KeyRangeStart, benchmarkPuzzle.KeyRangeEnd);
            var stopwatch = Stopwatch.StartNew();
            ulong checkedCandidates = 0;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var result = runtime.Search(ordinal, benchmarkPuzzle, range, checked((uint)count));
                checkedCandidates = checked(checkedCandidates + result.CheckedCandidates);
                if (!result.Found || !CandidateVerifier.Verify(benchmarkPuzzle, result.Candidate))
                {
                    throw new InvalidDataException("CUDA benchmark candidate failed CPU verification.");
                }
            }

            stopwatch.Stop();
            var keysPerSecond = checkedCandidates / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            Console.WriteLine($"CUDA benchmark passed on cuda:{ordinal}; puzzle={benchmarkPuzzle.Id}; iterations={iterations}; checked={checkedCandidates}; elapsed-ms={stopwatch.Elapsed.TotalMilliseconds:F0}; keys-per-second={keysPerSecond:F2}; candidate remained CPU-verified.");
            return 0;
        }

        runtime.RunIncrementSelfTest(ordinal);
        var testVectors = catalog.All
            .Where(static puzzle => puzzle.Kind == ApprovedTargetKind.TestVector)
            .OrderBy(static puzzle => puzzle.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var puzzle in testVectors)
        {
            runtime.RunPuzzleSelfTest(ordinal, puzzle, new WorkRange(puzzle.KeyRangeStart, puzzle.KeyRangeEnd));
        }

        Console.WriteLine($"CUDA puzzle self-test passed on cuda:{ordinal}; targets={string.Join(',', testVectors.Select(static puzzle => puzzle.Id))}; candidates remained CPU-verified.");
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or DllNotFoundException or MissingMethodException)
    {
        Console.Error.WriteLine($"Native operation rejected: {exception.Message}");
        return 2;
    }
}

static uint ParseDeviceOrdinal(IReadOnlyDictionary<string, string> options)
{
    if (!options.TryGetValue("device", out var value) ||
        !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
    {
        throw new ArgumentException("Option '--device' must be a non-negative integer.");
    }

    return ordinal;
}

static int RunScan(IReadOnlyDictionary<string, string> options, ApprovedPuzzle puzzle, SqliteCheckpointStore store)
{
    var keysPerPackage = ParsePositiveUlong(options, "chunk", 65_536);
    var checkpointEvery = ParsePositiveUlong(options, "checkpoint", Math.Min(keysPerPackage, 4_096));
    var maxHexRun = ParseBoundedNonNegativeInt(options, "max-hex-run", 0, 63);
    var workerSpec = options.GetValueOrDefault("worker") ?? "cpu";
    var isCpuWorker = string.Equals(workerSpec, "cpu", StringComparison.OrdinalIgnoreCase);
    uint requestedCudaOrdinal = 0;
    var isCudaWorker = workerSpec.StartsWith("cuda:", StringComparison.OrdinalIgnoreCase) &&
                       uint.TryParse(workerSpec[5..], NumberStyles.None, CultureInfo.InvariantCulture, out requestedCudaOrdinal);
    if (!isCpuWorker && !isCudaWorker)
    {
        throw new ArgumentException("--worker must be 'cpu' or 'cuda:<ordinal>'.");
    }

    if (isCudaWorker)
    {
        if (keysPerPackage > 1_048_576) throw new ArgumentException("CUDA --chunk cannot exceed 1048576.");
        if (checkpointEvery > 1_048_576) throw new ArgumentException("CUDA --checkpoint cannot exceed 1048576.");
    }

    var vaultPath = options.GetValueOrDefault("vault") ?? Path.Combine("checkpoints", $"{puzzle.Id}.vault");
    var password = Environment.GetEnvironmentVariable("TRPUZZLE_VAULT_PASSWORD");
    if (string.IsNullOrEmpty(password) || password.Length < 12)
    {
        Console.Error.WriteLine("Set TRPUZZLE_VAULT_PASSWORD (minimum 12 characters); it is never accepted as a CLI argument.");
        return 1;
    }

    store.EnsureScanProfile(puzzle, maxHexRun);
    var sessionId = store.CreateOrOpenSession(puzzle, keysPerPackage, DateTimeOffset.UtcNow);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var workerId = Environment.GetEnvironmentVariable("TRPUZZLE_WORKER_ID")
        ?? $"cpu-{Environment.MachineName}-{Environment.ProcessId}";
    var dashboard = new ScanDashboard(puzzle, isCudaWorker ? requestedCudaOrdinal : null, keysPerPackage, maxHexRun);
    dashboard.Start();
    var supervisorOptions = new CpuWorkerSupervisorOptions
    {
        KeysPerPackage = keysPerPackage,
        CheckpointEveryKeys = checkpointEvery,
        MaxHexRun = maxHexRun,
        Progress = dashboard.Report
    };
    Action<SearchResult> onFound = result =>
    {
        EncryptedResultVault.WriteNew(vaultPath, puzzle, result, password.AsSpan());
        dashboard.MarkFound(result.CandidateFingerprint, Path.GetFullPath(vaultPath), result.CheckedKeys);
    };

    SearchSessionSnapshot snapshot;
    if (isCpuWorker)
    {
        if (options.ContainsKey("native")) throw new ArgumentException("--native is only valid with --worker cuda:<ordinal>.");
        var supervisor = new CpuWorkerSupervisor(store, supervisorOptions);
        snapshot = supervisor.Run(puzzle, sessionId, workerId, onFound, cancellation.Token);
    }
    else if (isCudaWorker)
    {
        var ordinal = requestedCudaOrdinal;
        using var runtime = new NativeCudaRuntime(options.GetValueOrDefault("native"));
        if (!runtime.GetDevices().Any(device => device.Ordinal == ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(workerSpec), "Requested CUDA device is not available.");
        }

        workerId = Environment.GetEnvironmentVariable("TRPUZZLE_WORKER_ID")
            ?? $"cuda-{ordinal}-{Environment.MachineName}-{Environment.ProcessId}";
        var supervisor = new CudaWorkerSupervisor(store, runtime, ordinal, supervisorOptions);
        snapshot = supervisor.Run(puzzle, sessionId, workerId, onFound, cancellation.Token);
    }
    else
    {
        throw new ArgumentException("--worker must be 'cpu' or 'cuda:<ordinal>'.");
    }

    dashboard.Complete(snapshot);
    Console.WriteLine($"Session {snapshot.SessionId}: epoch={snapshot.Epoch}; state={snapshot.State}; checked={snapshot.CheckedKeys}; completed-packages={snapshot.CompletedPackages}");
    return 0;
}

static int RunStatus(ApprovedPuzzle puzzle, SqliteCheckpointStore store)
{
    var sessionId = store.GetSessionId(puzzle);
    if (sessionId is null)
    {
        Console.WriteLine($"No checkpoint session exists for {puzzle.Id}.");
        return 0;
    }

    var snapshot = store.GetSnapshot(puzzle, sessionId.Value);
    Console.WriteLine($"Session {snapshot.SessionId}: epoch={snapshot.Epoch}; state={snapshot.State}; pending={snapshot.PendingPackages}; leased={snapshot.LeasedPackages}; completed={snapshot.CompletedPackages}; checked={snapshot.CheckedKeys}");
    return 0;
}

static int StartNewEpoch(ApprovedPuzzle puzzle, SqliteCheckpointStore store)
{
    var sessionId = store.GetSessionId(puzzle);
    if (sessionId is null)
    {
        Console.Error.WriteLine($"No checkpoint session exists for {puzzle.Id}.");
        return 1;
    }

    var snapshot = store.StartNewEpoch(puzzle, sessionId.Value, DateTimeOffset.UtcNow);
    Console.WriteLine($"Started epoch {snapshot.Epoch} for {puzzle.Id}; state={snapshot.State}.");
    return 0;
}

static int RunVaultCommand(string command, IReadOnlyDictionary<string, string> options, ApprovedPuzzle puzzle)
{
    var vaultPath = options.GetValueOrDefault("vault") ?? Path.Combine("checkpoints", $"{puzzle.Id}.vault");
    var password = Environment.GetEnvironmentVariable("TRPUZZLE_VAULT_PASSWORD");
    if (string.IsNullOrEmpty(password) || password.Length < 12)
    {
        Console.Error.WriteLine("Set TRPUZZLE_VAULT_PASSWORD (minimum 12 characters); it is never accepted as a CLI argument.");
        return 1;
    }

    using var record = EncryptedResultVault.Read(vaultPath, puzzle, password.AsSpan());
    if (command == "vault-info")
    {
        Console.WriteLine($"Vault valid: puzzle={record.PuzzleId}; created={record.CreatedAt:O}; fingerprint={record.CandidateFingerprint}");
        return 0;
    }

    Console.Write("Type REVEAL to display the verified private key once: ");
    if (!string.Equals(Console.ReadLine(), "REVEAL", StringComparison.Ordinal))
    {
        Console.WriteLine("Reveal cancelled.");
        return 1;
    }

    Console.WriteLine("WARNING: the following value is a private key. Do not copy it to chat, logs or an unsecured file.");
    Console.WriteLine($"Puzzle: {record.PuzzleId}");
    Console.WriteLine($"Private key (hex): {record.PrivateKeyHex}");
    return 0;
}

static int SetPaused(ApprovedPuzzle puzzle, SqliteCheckpointStore store, bool paused)
{
    var sessionId = store.GetSessionId(puzzle);
    if (sessionId is null)
    {
        Console.Error.WriteLine($"No checkpoint session exists for {puzzle.Id}.");
        return 1;
    }

    store.SetPaused(puzzle, sessionId.Value, paused, DateTimeOffset.UtcNow);
    Console.WriteLine(paused ? $"Paused {puzzle.Id}." : $"Resumed {puzzle.Id}.");
    return 0;
}

static Dictionary<string, string> ParseOptions(string[] args, params string[] allowedNames)
{
    var allowed = allowedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var token = args[index];
        if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2 || index + 1 >= args.Length)
        {
            throw new ArgumentException("Options must use --name value form.");
        }

        var name = token[2..].ToLowerInvariant();
        if (!allowed.Contains(name))
        {
            throw new ArgumentException($"Unknown option '--{name}'.");
        }

        if (!result.TryAdd(name, args[++index])) throw new ArgumentException($"Option '--{name}' was supplied more than once.");
    }

    return result;
}

static ulong ParsePositiveUlong(IReadOnlyDictionary<string, string> options, string name, ulong fallback)
{
    if (!options.TryGetValue(name, out var value)) return fallback;
    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
    {
        throw new ArgumentException($"Option '--{name}' must be a positive unsigned integer.");
    }

    return parsed;
}

static int ParsePositiveInt(IReadOnlyDictionary<string, string> options, string name, int fallback, int minimum, int maximum)
{
    if (!options.TryGetValue(name, out var value)) return fallback;
    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
    {
        throw new ArgumentException($"Option '--{name}' must be between {minimum} and {maximum}.");
    }

    return parsed;
}

static int ParseBoundedNonNegativeInt(IReadOnlyDictionary<string, string> options, string name, int fallback, int maximum)
{
    if (!options.TryGetValue(name, out var value)) return fallback;
    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0 || parsed > maximum)
    {
        throw new ArgumentException($"Option '--{name}' must be between 0 and {maximum}.");
    }

    return parsed;
}

static void PrintUsage()
{
    Console.WriteLine("TrPuzzle - approved public BTC puzzles and built-in test vectors only");
    Console.WriteLine("Usage:");
    Console.WriteLine("  TrPuzzle.Service list");
    Console.WriteLine("  TrPuzzle.Service self-test");
    Console.WriteLine("  TrPuzzle.Service scan --puzzle <approved-id> [--worker cpu|cuda:<n>] [--native <path>] [--db <path>] [--chunk <n>] [--checkpoint <n>] [--max-hex-run <0-63>] [--vault <path>]");
    Console.WriteLine("  TrPuzzle.Service status --puzzle <approved-id> [--db <path>]");
    Console.WriteLine("  TrPuzzle.Service pause|resume --puzzle <approved-id> [--db <path>]");
    Console.WriteLine("  TrPuzzle.Service rescan --puzzle <approved-id> [--db <path>]  # start a new deterministic audit epoch");
    Console.WriteLine("  TrPuzzle.Service vault-info --puzzle <approved-id> [--vault <path>]");
    Console.WriteLine("  TrPuzzle.Service vault-reveal --puzzle <approved-id> [--vault <path>]  # explicit one-time display");
    Console.WriteLine("  TrPuzzle.Service devices [--native <path>]");
    Console.WriteLine("  TrPuzzle.Service gpu-self-test --device <n> [--native <path>]");
    Console.WriteLine("  TrPuzzle.Service gpu-benchmark --device <n> [--puzzle <approved-id>] [--iterations <n>] [--native <path>]");
    Console.WriteLine();
    Console.WriteLine("Arbitrary addresses, targets, WIF values and seed phrases are not accepted.");
}

sealed class ScanDashboard
{
    private readonly ApprovedPuzzle _puzzle;
    private readonly uint? _cudaDevice;
    private readonly ulong _keysPerPackage;
    private readonly int _maxHexRun;
    private readonly string _worker;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastRender = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTemperature = DateTimeOffset.MinValue;
    private string _temperature = "N/A";
    private string _state = "Starting";
    private string _message = "Waiting for the first checkpoint...";
    private string _fingerprint = string.Empty;
    private string _vaultPath = string.Empty;
    private WorkRange? _activeRange;
    private UInt256? _nextKey;
    private double _activePercent;
    private long _completedPackages;
    private System.Numerics.BigInteger _totalPackages;
    private ulong _totalChecked;
    private double _keysPerSecond;
    private TimeSpan _elapsed;
    private bool _interactive;
    private bool _finalized;
    private int _topRow;
    private const int PanelHeight = 16;

    public ScanDashboard(ApprovedPuzzle puzzle, uint? cudaDevice, ulong keysPerPackage, int maxHexRun)
    {
        _puzzle = puzzle;
        _cudaDevice = cudaDevice;
        _keysPerPackage = keysPerPackage;
        _maxHexRun = maxHexRun;
        _worker = cudaDevice is null ? "CPU" : $"CUDA:{cudaDevice}";
        _totalPackages = RangePlanner.GetChunkCount(puzzle, keysPerPackage);
        _interactive = !Console.IsOutputRedirected && !Console.IsErrorRedirected;
    }

    public void Start()
    {
        _state = "Running";
        if (_maxHexRun > 0) _message = $"FILTERED coverage: rejecting hexadecimal runs longer than {_maxHexRun}.";
        if (_cudaDevice is not null)
        {
            _temperature = ReadGpuTemperature(_cudaDevice.Value);
            _lastTemperature = DateTimeOffset.UtcNow;
        }

        if (_interactive)
        {
            try
            {
                Console.Clear();
                _topRow = Console.CursorTop;
                RenderPanel();
                return;
            }
            catch
            {
                _interactive = false;
            }
        }

        Console.WriteLine($"Searching approved target: {_puzzle.Id}; address={_puzzle.PublicAddress ?? "(built-in test vector)"}");
        Console.WriteLine($"Puzzle range: {_puzzle.KeyRangeStart}..{_puzzle.KeyRangeEnd}; worker={_worker}");
    }

    public void Report(ScanProgress progress)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRender < TimeSpan.FromSeconds(1)) return;
        _lastRender = now;
        if (_cudaDevice is not null && now - _lastTemperature >= TimeSpan.FromSeconds(5))
        {
            _temperature = ReadGpuTemperature(_cudaDevice.Value);
            _lastTemperature = now;
        }

        _activeRange = progress.ActiveRange;
        _nextKey = progress.NextKey;
        _activePercent = progress.RangeSize.IsZero
            ? 0d
            : (double)progress.CheckedInRange / (double)progress.RangeSize * 100d;
        _completedPackages = progress.CompletedPackages;
        _totalPackages = progress.TotalPackages;
        _totalChecked = progress.TotalChecked;
        _keysPerSecond = progress.KeysPerSecond;
        _elapsed = progress.Elapsed;
        _message = _maxHexRun > 0 ? "Scanning with incomplete filtered coverage." : "Scanning...";

        if (_interactive)
        {
            RenderPanel();
        }
        else
        {
            Console.WriteLine(BuildCompactLine());
        }
    }

    public void Complete(SearchSessionSnapshot snapshot)
    {
        _state = snapshot.State.ToString();
        _finalized = true;
        _totalChecked = snapshot.CheckedKeys;
        _completedPackages = snapshot.CompletedPackages;
        _elapsed = DateTimeOffset.UtcNow - _started;
        _keysPerSecond = snapshot.CheckedKeys / Math.Max(_elapsed.TotalSeconds, 0.001);
        _message = snapshot.State == SearchSessionState.Found
            ? "Verified candidate stored in encrypted vault."
            : _maxHexRun > 0 ? "Filtered scan finished; coverage is incomplete." : "Scan finished.";
        if (_interactive)
        {
            RenderPanel();
            MoveBelowPanel();
        }
        else
        {
            Console.WriteLine($"Scan finished: elapsed={_elapsed:hh\\:mm\\:ss}; total-checked={snapshot.CheckedKeys:N0}; state={snapshot.State}; gpu-temp={_temperature}");
        }
    }

    public void MarkFound(string fingerprint, string vaultPath, ulong checkedInRange)
    {
        _state = "Found";
        _message = "Verified candidate stored in encrypted vault.";
        _fingerprint = fingerprint;
        _vaultPath = vaultPath;
        _nextKey = null;
        _totalChecked = Math.Max(_totalChecked, checkedInRange);
        if (_activeRange is not null)
        {
            var activeRange = _activeRange.Value;
            var rangeSize = activeRange.End.ToBigInteger() - activeRange.Start.ToBigInteger() + System.Numerics.BigInteger.One;
            _activePercent = rangeSize.IsZero ? 0d : (double)checkedInRange / (double)rangeSize * 100d;
        }
        if (_interactive) RenderPanel();
        else Console.WriteLine($"Verified candidate stored in encrypted vault; fingerprint={fingerprint}; vault={vaultPath}");
    }

    private string BuildCompactLine() =>
        $"[{_elapsed:hh\\:mm\\:ss}] address={_puzzle.PublicAddress ?? "test-vector"} " +
        $"active={_activeRange?.Start.ToString() ?? "-"}..{_activeRange?.End.ToString() ?? "-"} " +
        $"next={_nextKey?.ToString() ?? "-"} active-progress={_activePercent:0.000000}% " +
        $"package={_completedPackages + 1}/{_totalPackages} checked={_totalChecked:N0} " +
        $"speed={_keysPerSecond:N0} keys/s gpu-temp={_temperature} filter={(_maxHexRun > 0 ? $"max-run-{_maxHexRun}" : "off")}";

    private void RenderPanel()
    {
        try
        {
            var activeStart = _activeRange?.Start.ToString() ?? "-";
            var activeEnd = _activeRange?.End.ToString() ?? "-";
            var next = _nextKey?.ToString() ?? "-";
            var packageNumber = _finalized ? _completedPackages : _completedPackages + 1;
            var package = $"{packageNumber}/{_totalPackages}";
            var progressBar = BuildProgressBar(_activePercent);
            WriteRow(0, "TRPUZZLE  |  FIXED SCAN DASHBOARD");
            WriteRow(1, $"Status       : {_state}    Worker: {_worker}    Coverage: {(_maxHexRun > 0 ? $"FILTERED max-run={_maxHexRun}" : "Full")}");
            WriteRow(2, $"Puzzle       : {_puzzle.Id}");
            WriteRow(3, $"Address      : {_puzzle.PublicAddress ?? "(built-in test vector)"}");
            WriteRow(4, $"Approved from: {_puzzle.KeyRangeStart}");
            WriteRow(5, $"Approved to  : {_puzzle.KeyRangeEnd}");
            WriteRow(6, $"Active from  : {activeStart}");
            WriteRow(7, $"Active to    : {activeEnd}");
            WriteRow(8, $"Next key     : {next}");
            WriteRow(9, $"Range        : {progressBar} {_activePercent,8:0.000000}%");
            WriteRow(10, $"Package      : {package}    Chunk size: {_keysPerPackage:N0}");
            WriteRow(11, $"Checked      : {_totalChecked:N0} keys    Speed: {_keysPerSecond:N0} keys/s");
            WriteRow(12, $"Runtime      : {_elapsed:hh\\:mm\\:ss}    GPU temperature: {_temperature}");
            WriteRow(13, $"Message      : {_message}");
            WriteRow(14, $"Fingerprint  : {_fingerprint}");
            WriteRow(15, $"Vault        : {_vaultPath}");
        }
        catch
        {
            _interactive = false;
            Console.WriteLine(BuildCompactLine());
        }
    }

    private void WriteRow(int offset, string value)
    {
        var width = GetConsoleWidth();
        var text = value.Length >= width ? value[..Math.Max(width - 1, 0)] : value.PadRight(width - 1);
        Console.SetCursorPosition(0, _topRow + offset);
        Console.Write(text);
    }

    private void MoveBelowPanel()
    {
        var row = Math.Min(_topRow + PanelHeight, Math.Max(Console.BufferHeight - 1, 0));
        Console.SetCursorPosition(0, row);
        Console.WriteLine();
    }

    private static string BuildProgressBar(double percent)
    {
        var clamped = Math.Clamp(percent, 0d, 100d);
        var filled = (int)Math.Round(clamped / 100d * 30d, MidpointRounding.ToZero);
        return "[" + new string('#', filled) + new string('-', 30 - filled) + "]";
    }

    private static int GetConsoleWidth()
    {
        try { return Math.Max(Console.WindowWidth, 80); }
        catch { return 120; }
    }

    private static string ReadGpuTemperature(uint device)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = $"--query-gpu=temperature.gpu --format=csv,noheader,nounits -i {device}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            if (!process.WaitForExit(1000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "N/A";
            }

            var value = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(value) ? "N/A" : value + " C";
        }
        catch
        {
            return "N/A";
        }
    }
}
