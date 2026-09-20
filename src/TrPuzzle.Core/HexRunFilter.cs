namespace TrPuzzle.Core;

public static class HexRunFilter
{
    public static bool Allows(UInt256 value, int maximumRunLength)
    {
        if (maximumRunLength == 0) return true;
        if (maximumRunLength is < 1 or > 63) throw new ArgumentOutOfRangeException(nameof(maximumRunLength));

        Span<byte> bytes = stackalloc byte[32];
        value.WriteBigEndian(bytes);
        var started = false;
        var previous = -1;
        var runLength = 0;
        foreach (var currentByte in bytes)
        {
            var high = currentByte >> 4;
            var low = currentByte & 0x0f;
            if (!Visit(high) || !Visit(low)) return false;
        }

        return true;

        bool Visit(int nibble)
        {
            if (!started && nibble == 0) return true;
            started = true;
            if (nibble == previous)
            {
                runLength++;
            }
            else
            {
                previous = nibble;
                runLength = 1;
            }

            return runLength <= maximumRunLength;
        }
    }
}
