using System.Security.Cryptography;

namespace TrPuzzle.Engine;

public sealed class SearchResult : IDisposable
{
    private byte[]? _privateKey;

    internal SearchResult(ulong checkedKeys, byte[]? privateKey)
    {
        CheckedKeys = checkedKeys;
        _privateKey = privateKey;
    }

    public ulong CheckedKeys { get; }
    public bool Found => _privateKey is not null;
    public string CandidateFingerprint => _privateKey is null
        ? string.Empty
        : Convert.ToHexString(SHA256.HashData(_privateKey).AsSpan(0, 8)).ToLowerInvariant();

    internal ReadOnlySpan<byte> DangerousPrivateKeySpan => _privateKey
        ?? throw new ObjectDisposedException(nameof(SearchResult));

    ~SearchResult() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (_privateKey is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_privateKey);
        _privateKey = null;
    }
}
