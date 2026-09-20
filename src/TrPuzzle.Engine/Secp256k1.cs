using System.Numerics;

namespace TrPuzzle.Engine;

internal static class Secp256k1
{
    private static readonly BigInteger P = FromHex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F");
    private static readonly BigInteger N = FromHex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");
    private static readonly AffinePoint Generator = new(
        FromHex("79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"),
        FromHex("483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8"));

    public static byte[] GetCompressedPublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != 32)
        {
            throw new ArgumentException("A secp256k1 private key must be exactly 32 bytes.", nameof(privateKey));
        }

        var scalar = new BigInteger(privateKey, isUnsigned: true, isBigEndian: true);
        if (scalar <= BigInteger.Zero || scalar >= N)
        {
            throw new ArgumentOutOfRangeException(nameof(privateKey), "Private key is outside the secp256k1 scalar field.");
        }

        var point = MultiplyGenerator(scalar);
        var result = new byte[33];
        result[0] = point.Y.IsEven ? (byte)0x02 : (byte)0x03;
        if (!point.X.TryWriteBytes(result.AsSpan(1), out var written, isUnsigned: true, isBigEndian: true))
        {
            throw new InvalidOperationException("Unable to encode the public key.");
        }

        if (written < 32)
        {
            result.AsSpan(1, written).CopyTo(result.AsSpan(33 - written));
            result.AsSpan(1, 32 - written).Clear();
        }

        return result;
    }

    private static AffinePoint MultiplyGenerator(BigInteger scalar)
    {
        var result = JacobianPoint.Infinity;
        var bitLength = (int)BigInteger.Log2(scalar) + 1;
        for (var bit = bitLength - 1; bit >= 0; bit--)
        {
            result = Double(result);
            if (((scalar >> bit) & BigInteger.One) != BigInteger.Zero)
            {
                result = AddMixed(result, Generator);
            }
        }

        if (result.IsInfinity)
        {
            throw new InvalidOperationException("Scalar multiplication produced the point at infinity.");
        }

        var zInverse = BigInteger.ModPow(result.Z, P - 2, P);
        var zInverseSquared = Mod(zInverse * zInverse);
        return new AffinePoint(
            Mod(result.X * zInverseSquared),
            Mod(result.Y * zInverseSquared * zInverse));
    }

    private static JacobianPoint Double(JacobianPoint point)
    {
        if (point.IsInfinity || point.Y.IsZero)
        {
            return JacobianPoint.Infinity;
        }

        var xx = Mod(point.X * point.X);
        var yy = Mod(point.Y * point.Y);
        var yyyy = Mod(yy * yy);
        var s = Mod(2 * (Mod((point.X + yy) * (point.X + yy)) - xx - yyyy));
        var m = Mod(3 * xx);
        var x = Mod(m * m - 2 * s);
        var y = Mod(m * (s - x) - 8 * yyyy);
        var z = Mod(2 * point.Y * point.Z);
        return new JacobianPoint(x, y, z);
    }

    private static JacobianPoint AddMixed(JacobianPoint left, AffinePoint right)
    {
        if (left.IsInfinity)
        {
            return new JacobianPoint(right.X, right.Y, BigInteger.One);
        }

        var zSquared = Mod(left.Z * left.Z);
        var u2 = Mod(right.X * zSquared);
        var s2 = Mod(right.Y * left.Z * zSquared);
        if (left.X == u2)
        {
            return left.Y == s2 ? Double(left) : JacobianPoint.Infinity;
        }

        var h = Mod(u2 - left.X);
        var hh = Mod(h * h);
        var i = Mod(4 * hh);
        var j = Mod(h * i);
        var r = Mod(2 * (s2 - left.Y));
        var v = Mod(left.X * i);
        var x = Mod(r * r - j - 2 * v);
        var y = Mod(r * (v - x) - 2 * left.Y * j);
        var z = Mod((left.Z + h) * (left.Z + h) - zSquared - hh);
        return new JacobianPoint(x, y, z);
    }

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % P;
        return result.Sign < 0 ? result + P : result;
    }

    private static BigInteger FromHex(string value) =>
        new(Convert.FromHexString(value), isUnsigned: true, isBigEndian: true);

    private readonly record struct AffinePoint(BigInteger X, BigInteger Y);

    private readonly record struct JacobianPoint(BigInteger X, BigInteger Y, BigInteger Z)
    {
        public static JacobianPoint Infinity => new(BigInteger.Zero, BigInteger.One, BigInteger.Zero);
        public bool IsInfinity => Z.IsZero;
    }
}
