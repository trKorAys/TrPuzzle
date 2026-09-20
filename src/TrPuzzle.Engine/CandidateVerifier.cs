using System.Security.Cryptography;
using TrPuzzle.Core;

namespace TrPuzzle.Engine;

public static class CandidateVerifier
{
    public static bool Verify(ApprovedPuzzle puzzle, UInt256 candidate)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        if (candidate < puzzle.KeyRangeStart || candidate > puzzle.KeyRangeEnd)
        {
            return false;
        }

        Span<byte> privateKey = stackalloc byte[32];
        byte[]? publicKey = null;
        byte[]? sha256 = null;
        byte[]? hash160 = null;
        try
        {
            candidate.WriteBigEndian(privateKey);
            publicKey = Secp256k1.GetCompressedPublicKey(privateKey);
            sha256 = SHA256.HashData(publicKey);
            hash160 = Ripemd160.Hash(sha256);
            return CryptographicOperations.FixedTimeEquals(hash160, puzzle.TargetHash160Span);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
            if (sha256 is not null) CryptographicOperations.ZeroMemory(sha256);
            if (hash160 is not null) CryptographicOperations.ZeroMemory(hash160);
        }
    }
}
