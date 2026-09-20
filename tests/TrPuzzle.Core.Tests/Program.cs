using System.Numerics;
using TrPuzzle.Core;

Run("UInt256 preserves unsigned high bit", () =>
{
    var value = UInt256.Parse("8000000000000000000000000000000000000000000000000000000000000000");
    Equal(BigInteger.One << 255, value.ToBigInteger());
    Equal("8000000000000000000000000000000000000000000000000000000000000000", value.ToString());
});

Run("UInt256 increment carries and detects overflow", () =>
{
    var value = UInt256.Parse("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe");
    True(value.TryIncrement(out var maximum));
    Equal("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", maximum.ToString());
    True(!maximum.TryIncrement(out _));
});

Run("UInt256 BigInteger conversion right-aligns short values", () =>
{
    var value = UInt256.FromBigInteger(new BigInteger(16));
    Equal("0000000000000000000000000000000000000000000000000000000000000010", value.ToString());
    Equal(new BigInteger(16), value.ToBigInteger());
});

Run("UInt256 addition carries without wrapping", () =>
{
    var value = UInt256.Parse("ffffffffffffffffffffffffffffffffffffffffffffffff0000000000000000");
    True(value.TryAdd(ulong.MaxValue, out var sum));
    Equal("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", sum.ToString());
    True(!sum.TryAdd(1, out _));
});

Run("Hex run filter ignores fixed leading zero padding", () =>
{
    True(HexRunFilter.Allows(UInt256.Parse("1000111"), 3));
    True(!HexRunFilter.Allows(UInt256.Parse("10001111"), 3));
    True(HexRunFilter.Allows(UInt256.Parse("0000000000000000000000000000000000000000000000000000000000000010"), 3));
});

Run("Embedded catalog exposes only approved public puzzles and test vectors", () =>
{
    var catalog = ApprovedPuzzleCatalog.LoadEmbedded();
    Equal(165, catalog.All.Count);
    Equal(160, catalog.All.Count(static puzzle => puzzle.Kind == ApprovedTargetKind.PublicPuzzle));
    Equal(5, catalog.All.Count(static puzzle => puzzle.Kind == ApprovedTargetKind.TestVector));
    var publicPuzzles = catalog.All
        .Where(static puzzle => puzzle.Kind == ApprovedTargetKind.PublicPuzzle)
        .OrderBy(static puzzle => int.Parse(puzzle.Id[11..], System.Globalization.CultureInfo.InvariantCulture))
        .ToArray();
    for (var index = 1; index <= publicPuzzles.Length; index++)
    {
        var puzzle = publicPuzzles[index - 1];
        Equal($"btc-puzzle-{index}", puzzle.Id);
        Equal(BigInteger.One << (index - 1), puzzle.KeyRangeStart.ToBigInteger());
        Equal((BigInteger.One << index) - BigInteger.One, puzzle.KeyRangeEnd.ToBigInteger());
        Equal(20, puzzle.TargetHash160.Length);
        True(!string.IsNullOrWhiteSpace(puzzle.PublicAddress));
    }
    Throws<KeyNotFoundException>(() => catalog.GetRequired("user-supplied-target"));
});

Run("Work range cannot escape approved bounds", () =>
{
    var puzzle = ApprovedPuzzleCatalog.LoadEmbedded().GetRequired("test-scalar-1");
    var outside = new WorkRange(UInt256.Parse("1"), UInt256.Parse("9"));
    Throws<InvalidOperationException>(() => outside.EnsureWithin(puzzle));
});

Console.WriteLine("TrPuzzle.Core.Tests: all checks passed.");

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
