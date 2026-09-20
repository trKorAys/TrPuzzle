using TrPuzzle.Core;
using TrPuzzle.Engine;

namespace TrPuzzle.NativeBridge;

/// <summary>
/// Security boundary for future CUDA integration. Native code may propose a candidate;
/// only this managed layer can accept it after independent CPU verification.
/// </summary>
public sealed class NativeSearchCoordinator
{
    private readonly NativeCudaRuntime? _runtime;
    private readonly uint _ordinal;

    public NativeSearchCoordinator()
    {
    }

    public NativeSearchCoordinator(NativeCudaRuntime runtime, uint ordinal)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _ordinal = ordinal;
    }

    public bool IsAvailable => _runtime is not null;

    public SearchResult Search(ApprovedPuzzle puzzle, WorkRange range, int maxHexRun = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        range.EnsureWithin(puzzle);
        if (maxHexRun is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(maxHexRun));
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime is null) throw new PlatformNotSupportedException("CUDA runtime is not configured.");

        var count = range.End.ToBigInteger() - range.Start.ToBigInteger() + System.Numerics.BigInteger.One;
        if (count <= 0 || count > 1_048_576) throw new ArgumentOutOfRangeException(nameof(range), "CUDA search packages must contain at most 1048576 candidates.");
        var nativeResult = _runtime.Search(_ordinal, puzzle, range, checked((uint)count), maxHexRun);
        cancellationToken.ThrowIfCancellationRequested();
        if (!nativeResult.Found) return new SearchResult(nativeResult.CheckedCandidates, privateKey: null);
        if (!HexRunFilter.Allows(nativeResult.Candidate, maxHexRun))
        {
            throw new InvalidDataException("CUDA candidate violated the configured hexadecimal run filter.");
        }
        if (!CandidateVerifier.Verify(puzzle, nativeResult.Candidate))
        {
            throw new InvalidDataException("CUDA candidate failed independent CPU verification.");
        }

        return new SearchResult(nativeResult.CheckedCandidates, nativeResult.Candidate.ToBigEndianBytes());
    }

    internal static SearchResult VerifyNativeCandidate(
        ApprovedPuzzle puzzle,
        WorkRange range,
        UInt256 candidate,
        ulong checkedKeys)
    {
        range.EnsureWithin(puzzle);
        if (candidate < range.Start || candidate > range.End)
        {
            throw new InvalidDataException("Native worker returned a candidate outside its assigned work range.");
        }

        if (!CandidateVerifier.Verify(puzzle, candidate))
        {
            throw new InvalidDataException("Native worker candidate failed independent CPU verification.");
        }

        return new SearchResult(checkedKeys, candidate.ToBigEndianBytes());
    }
}
