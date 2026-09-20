using System.Security.Cryptography;

namespace TrPuzzle.Core;

internal static class BitcoinAddress
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static bool TryDecodeMainnetP2pkh(string address, out byte[] hash160)
    {
        hash160 = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(address)) return false;

        var decoded = new List<byte>();
        foreach (var character in address)
        {
            var digit = Alphabet.IndexOf(character);
            if (digit < 0) return false;

            var carry = digit;
            for (var index = decoded.Count - 1; index >= 0; index--)
            {
                var value = decoded[index] * 58 + carry;
                decoded[index] = (byte)(value & 0xff);
                carry = value >> 8;
            }

            while (carry > 0)
            {
                decoded.Insert(0, (byte)(carry & 0xff));
                carry >>= 8;
            }
        }

        var leadingZeroes = address.TakeWhile(static character => character == '1').Count();
        var payload = new byte[leadingZeroes + decoded.Count];
        for (var index = 0; index < decoded.Count; index++)
        {
            payload[leadingZeroes + index] = decoded[index];
        }

        if (payload.Length != 25 || payload[0] != 0) return false;
        var checksum = SHA256.HashData(SHA256.HashData(payload.AsSpan(0, 21)));
        if (!CryptographicOperations.FixedTimeEquals(payload.AsSpan(21, 4), checksum.AsSpan(0, 4))) return false;

        hash160 = payload[1..21];
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(checksum);
        return true;
    }
}
