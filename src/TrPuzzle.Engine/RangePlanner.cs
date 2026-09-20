using System.Numerics;
using System.Security.Cryptography;
using TrPuzzle.Core;

namespace TrPuzzle.Engine;

public static class RangePlanner
{
    public static IEnumerable<WorkPackage> Plan(ApprovedPuzzle puzzle, ulong keysPerPackage)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        if (keysPerPackage == 0) throw new ArgumentOutOfRangeException(nameof(keysPerPackage));

        var start = puzzle.KeyRangeStart;
        long sequence = 0;
        while (true)
        {
            var hasCandidateEnd = start.TryAdd(keysPerPackage - 1, out var candidateEnd);
            var end = !hasCandidateEnd || candidateEnd > puzzle.KeyRangeEnd
                ? puzzle.KeyRangeEnd
                : candidateEnd;

            yield return new WorkPackage(puzzle.Id, sequence, new WorkRange(start, end));
            if (end == puzzle.KeyRangeEnd)
            {
                yield break;
            }

            if (!end.TryIncrement(out start))
            {
                throw new InvalidOperationException("Range planner overflowed before reaching the approved boundary.");
            }

            sequence = checked(sequence + 1);
        }
    }

    public static BigInteger GetChunkCount(ApprovedPuzzle puzzle, ulong keysPerPackage)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        if (keysPerPackage == 0) throw new ArgumentOutOfRangeException(nameof(keysPerPackage));

        var total = puzzle.KeyRangeEnd.ToBigInteger() - puzzle.KeyRangeStart.ToBigInteger() + BigInteger.One;
        return (total + keysPerPackage - BigInteger.One) / keysPerPackage;
    }

    public static WorkRange GetChunkRange(ApprovedPuzzle puzzle, ulong keysPerPackage, BigInteger chunkIndex)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        if (keysPerPackage == 0) throw new ArgumentOutOfRangeException(nameof(keysPerPackage));
        if (chunkIndex.Sign < 0 || chunkIndex >= GetChunkCount(puzzle, keysPerPackage))
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        var start = puzzle.KeyRangeStart.ToBigInteger() + chunkIndex * keysPerPackage;
        var end = BigInteger.Min(
            start + keysPerPackage - BigInteger.One,
            puzzle.KeyRangeEnd.ToBigInteger());
        return new WorkRange(UInt256.FromBigInteger(start), UInt256.FromBigInteger(end));
    }

    /// <summary>
    /// Returns a reproducible, one-to-one permutation of chunk indices for an epoch.
    /// The seed is persisted with the session; no random state is kept in memory.
    /// </summary>
    public static BigInteger PermuteChunkIndex(ReadOnlySpan<byte> seed, BigInteger cursor, BigInteger chunkCount)
    {
        if (seed.Length != 32) throw new ArgumentException("Epoch seed must be exactly 32 bytes.", nameof(seed));
        if (chunkCount <= BigInteger.Zero) throw new ArgumentOutOfRangeException(nameof(chunkCount));
        if (cursor.Sign < 0 || cursor >= chunkCount) throw new ArgumentOutOfRangeException(nameof(cursor));
        if (chunkCount == BigInteger.One) return BigInteger.Zero;

        var multiplier = Derive(seed, 0xA1, chunkCount);
        if (multiplier.IsZero) multiplier = BigInteger.One;
        var attempts = 0;
        while (BigInteger.GreatestCommonDivisor(multiplier, chunkCount) != BigInteger.One)
        {
            multiplier = (multiplier + BigInteger.One) % chunkCount;
            if (++attempts > 1_000_000) throw new InvalidOperationException("Unable to derive a valid epoch permutation.");
        }

        var offset = Derive(seed, 0xB7, chunkCount);
        return (multiplier * cursor + offset) % chunkCount;
    }

    public static byte[] CreateSeed()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    public static string SeedToHex(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("Epoch seed must be exactly 32 bytes.", nameof(seed));
        return Convert.ToHexString(seed).ToLowerInvariant();
    }

    public static byte[] SeedFromHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var seed = Convert.FromHexString(hex);
        if (seed.Length != 32) throw new FormatException("Epoch seed must contain exactly 32 bytes.");
        return seed;
    }

    private static BigInteger Derive(ReadOnlySpan<byte> seed, byte discriminator, BigInteger modulus)
    {
        Span<byte> input = stackalloc byte[33];
        seed.CopyTo(input);
        input[32] = discriminator;
        var digest = SHA256.HashData(input);
        return new BigInteger(digest, isUnsigned: true, isBigEndian: true) % modulus;
    }
}
