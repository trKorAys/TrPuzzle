using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace TrPuzzle.Core;

/// <summary>A fixed-width, unsigned 256-bit value with no signed conversion ambiguity.</summary>
public readonly struct UInt256 : IComparable<UInt256>, IEquatable<UInt256>
{
    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    private UInt256(ulong a, ulong b, ulong c, ulong d) => (_a, _b, _c, _d) = (a, b, c, d);

    public static UInt256 Zero => default;

    public static UInt256 Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var normalized = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (normalized.Length is 0 or > 64 || normalized.Any(static c => !Uri.IsHexDigit(c)))
        {
            throw new FormatException("UInt256 must contain between 1 and 64 hexadecimal characters.");
        }

        normalized = normalized.PadLeft(64, '0');
        return new UInt256(
            ulong.Parse(normalized.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            ulong.Parse(normalized.AsSpan(16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            ulong.Parse(normalized.AsSpan(32, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
            ulong.Parse(normalized.AsSpan(48, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
    }

    public static UInt256 FromBigEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "UInt256 cannot exceed 32 bytes.");
        }

        Span<byte> padded = stackalloc byte[32];
        bytes.CopyTo(padded[(32 - bytes.Length)..]);
        return new UInt256(
            BinaryPrimitives.ReadUInt64BigEndian(padded),
            BinaryPrimitives.ReadUInt64BigEndian(padded[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(padded[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(padded[24..]));
    }

    public static UInt256 FromBigInteger(BigInteger value)
    {
        if (value.Sign < 0 || value.GetByteCount(isUnsigned: true) > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Span<byte> bytes = stackalloc byte[32];
        var byteCount = value.GetByteCount(isUnsigned: true);
        if (!value.TryWriteBytes(bytes[(32 - byteCount)..], out _, isUnsigned: true, isBigEndian: true))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return FromBigEndian(bytes);
    }

    public BigInteger ToBigInteger()
    {
        Span<byte> bytes = stackalloc byte[32];
        WriteBigEndian(bytes);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    public void WriteBigEndian(Span<byte> destination)
    {
        if (destination.Length < 32)
        {
            throw new ArgumentException("Destination must be at least 32 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _d);
    }

    public byte[] ToBigEndianBytes()
    {
        var result = new byte[32];
        WriteBigEndian(result);
        return result;
    }

    public bool TryIncrement(out UInt256 incremented)
    {
        var d = _d + 1;
        var carryD = d == 0;
        var c = _c + (carryD ? 1UL : 0UL);
        var carryC = carryD && c == 0;
        var b = _b + (carryC ? 1UL : 0UL);
        var carryB = carryC && b == 0;
        var a = _a + (carryB ? 1UL : 0UL);
        if (carryB && a == 0)
        {
            incremented = default;
            return false;
        }

        incremented = new UInt256(a, b, c, d);
        return true;
    }

    public bool TryAdd(ulong value, out UInt256 sum)
    {
        var d = _d + value;
        var carryD = d < _d;
        var c = _c + (carryD ? 1UL : 0UL);
        var carryC = carryD && c == 0;
        var b = _b + (carryC ? 1UL : 0UL);
        var carryB = carryC && b == 0;
        var a = _a + (carryB ? 1UL : 0UL);
        if (carryB && a == 0)
        {
            sum = default;
            return false;
        }

        sum = new UInt256(a, b, c, d);
        return true;
    }

    public int CompareTo(UInt256 other)
    {
        var comparison = _a.CompareTo(other._a);
        if (comparison != 0) return comparison;
        comparison = _b.CompareTo(other._b);
        if (comparison != 0) return comparison;
        comparison = _c.CompareTo(other._c);
        return comparison != 0 ? comparison : _d.CompareTo(other._d);
    }

    public bool Equals(UInt256 other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;
    public override bool Equals(object? obj) => obj is UInt256 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);
    public override string ToString() => $"{_a:x16}{_b:x16}{_c:x16}{_d:x16}";

    public static bool operator ==(UInt256 left, UInt256 right) => left.Equals(right);
    public static bool operator !=(UInt256 left, UInt256 right) => !left.Equals(right);
    public static bool operator <(UInt256 left, UInt256 right) => left.CompareTo(right) < 0;
    public static bool operator >(UInt256 left, UInt256 right) => left.CompareTo(right) > 0;
    public static bool operator <=(UInt256 left, UInt256 right) => left.CompareTo(right) <= 0;
    public static bool operator >=(UInt256 left, UInt256 right) => left.CompareTo(right) >= 0;
}
