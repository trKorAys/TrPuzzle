using System.Buffers.Binary;
using System.Numerics;

namespace TrPuzzle.Engine;

internal static class Ripemd160
{
    private static readonly byte[] LeftIndexes =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
        4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13
    ];

    private static readonly byte[] RightIndexes =
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
        12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11
    ];

    private static readonly byte[] LeftRotations =
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
        9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6
    ];

    private static readonly byte[] RightRotations =
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
        8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11
    ];

    public static byte[] Hash(ReadOnlySpan<byte> source)
    {
        var paddingLength = (56 - ((source.Length + 1) % 64) + 64) % 64;
        var padded = new byte[source.Length + 1 + paddingLength + 8];
        source.CopyTo(padded);
        padded[source.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), checked((ulong)source.Length * 8));

        uint h0 = 0x67452301;
        uint h1 = 0xefcdab89;
        uint h2 = 0x98badcfe;
        uint h3 = 0x10325476;
        uint h4 = 0xc3d2e1f0;
        Span<uint> words = stackalloc uint[16];

        for (var offset = 0; offset < padded.Length; offset += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                words[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(offset + i * 4, 4));
            }

            var al = h0; var bl = h1; var cl = h2; var dl = h3; var el = h4;
            var ar = h0; var br = h1; var cr = h2; var dr = h3; var er = h4;

            for (var round = 0; round < 80; round++)
            {
                var tl = BitOperations.RotateLeft(unchecked(al + Function(round, bl, cl, dl) + words[LeftIndexes[round]] + LeftConstant(round)), LeftRotations[round]);
                tl = unchecked(tl + el);
                al = el; el = dl; dl = BitOperations.RotateLeft(cl, 10); cl = bl; bl = tl;

                var tr = BitOperations.RotateLeft(unchecked(ar + Function(79 - round, br, cr, dr) + words[RightIndexes[round]] + RightConstant(round)), RightRotations[round]);
                tr = unchecked(tr + er);
                ar = er; er = dr; dr = BitOperations.RotateLeft(cr, 10); cr = br; br = tr;
            }

            var temporary = unchecked(h1 + cl + dr);
            h1 = unchecked(h2 + dl + er);
            h2 = unchecked(h3 + el + ar);
            h3 = unchecked(h4 + al + br);
            h4 = unchecked(h0 + bl + cr);
            h0 = temporary;
        }

        var result = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(result, h0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), h1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), h2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), h3);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), h4);
        return result;
    }

    private static uint Function(int round, uint x, uint y, uint z) => round switch
    {
        < 16 => x ^ y ^ z,
        < 32 => (x & y) | (~x & z),
        < 48 => (x | ~y) ^ z,
        < 64 => (x & z) | (y & ~z),
        _ => x ^ (y | ~z)
    };

    private static uint LeftConstant(int round) => round switch
    {
        < 16 => 0x00000000,
        < 32 => 0x5a827999,
        < 48 => 0x6ed9eba1,
        < 64 => 0x8f1bbcdc,
        _ => 0xa953fd4e
    };

    private static uint RightConstant(int round) => round switch
    {
        < 16 => 0x50a28be6,
        < 32 => 0x5c4dd124,
        < 48 => 0x6d703ef3,
        < 64 => 0x7a6d76e9,
        _ => 0x00000000
    };
}
