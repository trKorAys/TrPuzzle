using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TrPuzzle.Core;
using TrPuzzle.Engine;

namespace TrPuzzle.NativeBridge;

public sealed record CudaDeviceInfo(
    uint Ordinal,
    string Name,
    ulong TotalMemoryBytes,
    int ComputeMajor,
    int ComputeMinor);

public sealed record CudaSearchResult(bool Found, UInt256 Candidate, ulong CheckedCandidates);

/// <summary>Versioned, narrow loader for the native CUDA ABI.</summary>
public sealed class NativeCudaRuntime : IDisposable
{
    private const uint ExpectedAbiVersion = 3;
    private IntPtr _library;
    private readonly GetAbiDelegate _getAbi;
    private readonly GetCountDelegate _getCount;
    private readonly GetInfoDelegate _getInfo;
    private readonly IncrementDelegate _increment;
    private readonly SearchRangeDelegate _searchRange;
    private readonly LastErrorDelegate _lastError;

    public NativeCudaRuntime(string? libraryPath = null)
    {
        _library = LoadLibrary(libraryPath);
        try
        {
            _getAbi = Bind<GetAbiDelegate>("trpuzzle_abi_version");
            _getCount = Bind<GetCountDelegate>("trpuzzle_cuda_device_count");
            _getInfo = Bind<GetInfoDelegate>("trpuzzle_cuda_get_device_info");
            _increment = Bind<IncrementDelegate>("trpuzzle_cuda_increment256");
            _searchRange = Bind<SearchRangeDelegate>("trpuzzle_cuda_search_range");
            _lastError = Bind<LastErrorDelegate>("trpuzzle_last_error");
            var abi = _getAbi();
            if (abi != ExpectedAbiVersion)
            {
                throw new InvalidDataException($"Native ABI {abi} is incompatible; expected {ExpectedAbiVersion}.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyList<CudaDeviceInfo> GetDevices()
    {
        var count = _getCount();
        var devices = new List<CudaDeviceInfo>(checked((int)count));
        for (uint ordinal = 0; ordinal < count; ordinal++)
        {
            var native = new NativeDeviceInfo { StructSize = checked((uint)Marshal.SizeOf<NativeDeviceInfo>()) };
            if (_getInfo(ordinal, ref native) != 0) throw new InvalidOperationException(GetLastError());
            devices.Add(new CudaDeviceInfo(ordinal, native.Name ?? string.Empty, native.TotalMemoryBytes, native.ComputeMajor, native.ComputeMinor));
        }

        return devices;
    }

    public UInt256 Increment(uint ordinal, UInt256 value)
    {
        var input = value.ToBigEndianBytes();
        var output = new byte[32];
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            if (_increment(ordinal, inputHandle.AddrOfPinnedObject(), outputHandle.AddrOfPinnedObject()) != 0)
            {
                throw new InvalidOperationException(GetLastError());
            }

            return UInt256.FromBigEndian(output);
        }
        finally
        {
            outputHandle.Free();
            inputHandle.Free();
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(output);
        }
    }

    public void RunIncrementSelfTest(uint ordinal)
    {
        var vectors = new[]
        {
            UInt256.Zero,
            UInt256.Parse("000000000000000000000000000000000000000000000000000000000000000f"),
            UInt256.Parse("00000000000000000000000000000000000000000000000000000000000000ff"),
            UInt256.Parse("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe")
        };
        foreach (var input in vectors)
        {
            if (!input.TryIncrement(out var expected)) throw new InvalidOperationException("Self-test vector unexpectedly overflowed.");
            var actual = Increment(ordinal, input);
            if (actual != expected) throw new InvalidDataException($"CUDA increment self-test failed for vector {input}.");
        }
    }

    public CudaSearchResult Search(uint ordinal, ApprovedPuzzle puzzle, WorkRange range, uint maxCandidates, int maxHexRun = 0)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        range.EnsureWithin(puzzle);
        if (maxCandidates == 0 || maxCandidates > (1U << 20)) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        if (maxHexRun is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(maxHexRun));

        var start = range.Start.ToBigEndianBytes();
        var end = range.End.ToBigEndianBytes();
        var target = puzzle.TargetHash160.ToArray();
        var found = new byte[32];
        var checkedCandidates = new ulong[1];
        var handles = new[]
        {
            GCHandle.Alloc(start, GCHandleType.Pinned),
            GCHandle.Alloc(end, GCHandleType.Pinned),
            GCHandle.Alloc(target, GCHandleType.Pinned),
            GCHandle.Alloc(found, GCHandleType.Pinned),
            GCHandle.Alloc(checkedCandidates, GCHandleType.Pinned)
        };
        try
        {
            var status = _searchRange(
                ordinal,
                handles[0].AddrOfPinnedObject(),
                handles[1].AddrOfPinnedObject(),
                handles[2].AddrOfPinnedObject(),
                maxCandidates,
                checked((uint)maxHexRun),
                handles[3].AddrOfPinnedObject(),
                handles[4].AddrOfPinnedObject());
            if (status < 0) throw new InvalidOperationException(GetLastError());
            return new CudaSearchResult(status == 1, UInt256.FromBigEndian(found), checkedCandidates[0]);
        }
        finally
        {
            foreach (var handle in handles) handle.Free();
            CryptographicOperations.ZeroMemory(start);
            CryptographicOperations.ZeroMemory(end);
            CryptographicOperations.ZeroMemory(target);
            CryptographicOperations.ZeroMemory(found);
        }
    }

    public void RunPuzzleSelfTest(uint ordinal, ApprovedPuzzle puzzle, WorkRange range)
    {
        var expected = puzzle.TargetHash160.ToArray();
        var result = Search(ordinal, puzzle, range, 1_024);
        if (!result.Found || !CandidateVerifier.Verify(puzzle, result.Candidate))
        {
            throw new InvalidDataException("CUDA puzzle self-test did not produce a CPU-verifiable candidate.");
        }
    }

    public void Dispose()
    {
        if (_library == IntPtr.Zero) return;
        NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    ~NativeCudaRuntime() => Dispose();

    private T Bind<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_library, name, out var address))
        {
            throw new MissingMethodException($"Native ABI export '{name}' is missing.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private string GetLastError()
    {
        var buffer = new byte[512];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            _lastError(handle.AddrOfPinnedObject(), checked((uint)buffer.Length));
            var length = Array.IndexOf(buffer, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }
        finally
        {
            handle.Free();
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static IntPtr LoadLibrary(string? libraryPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(libraryPath)) candidates.Add(libraryPath);
        var environmentPath = Environment.GetEnvironmentVariable("TRPUZZLE_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(environmentPath)) candidates.Add(environmentPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "trpuzzle_native.dll"));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var loaded)) return loaded;
        }

        if (NativeLibrary.TryLoad("trpuzzle_native", out var byName)) return byName;
        throw new DllNotFoundException("TrPuzzle.Native could not be loaded. Set TRPUZZLE_NATIVE_LIBRARY to the ABI-matched DLL.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeDeviceInfo
    {
        public uint StructSize;
        public uint Ordinal;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Name;
        public ulong TotalMemoryBytes;
        public int ComputeMajor;
        public int ComputeMinor;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetAbiDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetCountDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetInfoDelegate(uint ordinal, ref NativeDeviceInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IncrementDelegate(uint ordinal, IntPtr input, IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SearchRangeDelegate(uint ordinal, IntPtr start, IntPtr end, IntPtr target, uint maxCandidates, uint maxHexRun, IntPtr found, IntPtr checkedCandidates);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LastErrorDelegate(IntPtr buffer, uint bufferSize);
}
