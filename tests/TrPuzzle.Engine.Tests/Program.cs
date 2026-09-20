using System.Security.Cryptography;
using TrPuzzle.Core;
using TrPuzzle.Engine;

Run("RIPEMD-160 standard vector", () =>
{
    Equal("9c1185a5c5e9fc54612808977ee8f548b2258d31", Convert.ToHexString(Ripemd160.Hash([])).ToLowerInvariant());
});

Run("secp256k1 scalar 1 compressed public key", () =>
{
    Span<byte> key = stackalloc byte[32];
    key[31] = 1;
    var publicKey = Secp256k1.GetCompressedPublicKey(key);
    Equal("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", Convert.ToHexString(publicKey).ToLowerInvariant());
    var hash160 = Ripemd160.Hash(SHA256.HashData(publicKey));
    Equal("751e76e8199196d454941c45d1b3a323f1433bd6", Convert.ToHexString(hash160).ToLowerInvariant());
});

Run("secp256k1 doubling and addition vectors", () =>
{
    Span<byte> key = stackalloc byte[32];
    key[31] = 2;
    Equal(
        "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5",
        Convert.ToHexString(Secp256k1.GetCompressedPublicKey(key)).ToLowerInvariant());
    key[31] = 3;
    Equal(
        "02f9308a019258c31049344f85f89d5229b531c845836f99b08601f113bce036f9",
        Convert.ToHexString(Secp256k1.GetCompressedPublicKey(key)).ToLowerInvariant());
});

Run("CPU search finds scalar 1 without exposing it as text", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-1");
    using var result = new CpuReferenceSearcher().Search(puzzle, new WorkRange(puzzle.KeyRangeStart, puzzle.KeyRangeEnd));
    True(result.Found);
    Equal(1UL, result.CheckedKeys);
    True(!result.ToString()!.Contains(puzzle.KeyRangeStart.ToString(), StringComparison.Ordinal));
});

Run("CPU range search finds scalar 16", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-16");
    using var result = new CpuReferenceSearcher().Search(puzzle, new WorkRange(puzzle.KeyRangeStart, puzzle.KeyRangeEnd));
    True(result.Found);
    Equal(8UL, result.CheckedKeys);
});

Run("Encrypted vault round trip decrypts only a CPU-verified candidate", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-16");
    var path = Path.Combine(Path.GetTempPath(), $"trpuzzle-vault-{Guid.NewGuid():N}.vault");
    try
    {
        using var result = new CpuReferenceSearcher().Search(puzzle, new WorkRange(puzzle.KeyRangeStart, puzzle.KeyRangeEnd));
        EncryptedResultVault.WriteNew(path, puzzle, result, "test-vault-password-123".AsSpan());
        using var record = EncryptedResultVault.Read(path, puzzle, "test-vault-password-123".AsSpan());
        Equal(UInt256.Parse("10"), record.Candidate);
        True(CandidateVerifier.Verify(puzzle, record.Candidate));
        Throws<InvalidDataException>(() => EncryptedResultVault.Read(path, puzzle, "wrong-vault-password".AsSpan()));
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
});

Run("Range planner produces contiguous inclusive packages", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-16");
    var packages = RangePlanner.Plan(puzzle, 5).ToArray();
    Equal(4, packages.Length);
    Equal("0000000000000000000000000000000000000000000000000000000000000009", packages[0].Range.Start.ToString());
    Equal("000000000000000000000000000000000000000000000000000000000000000d", packages[0].Range.End.ToString());
    Equal("0000000000000000000000000000000000000000000000000000000000000018", packages[^1].Range.End.ToString());
    for (var index = 1; index < packages.Length; index++)
    {
        True(packages[index - 1].Range.End.TryIncrement(out var expectedStart));
        Equal(expectedStart, packages[index].Range.Start);
        Equal((long)index, packages[index].Sequence);
    }
});

Run("Epoch permutation is deterministic and covers every chunk once", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-16");
    var seed = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    var chunkCount = RangePlanner.GetChunkCount(puzzle, 3);
    Equal(new System.Numerics.BigInteger(6), chunkCount);
    var indices = Enumerable.Range(0, 6)
        .Select(index => RangePlanner.PermuteChunkIndex(seed, index, chunkCount))
        .ToArray();
    Equal(6, indices.Distinct().Count());
    Equal(indices[0], RangePlanner.PermuteChunkIndex(seed, 0, chunkCount));
    var chunk = RangePlanner.GetChunkRange(puzzle, 3, indices[0]);
    chunk.EnsureWithin(puzzle);
});

Console.WriteLine("TrPuzzle.Engine.Tests: all checks passed.");

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
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
