using System.Security.Cryptography;
using System.Text;

namespace TrPuzzle.Core;

public enum ApprovedTargetKind
{
    PublicPuzzle,
    TestVector
}

/// <summary>
/// A target created only by <see cref="ApprovedPuzzleCatalog"/> from embedded manifests.
/// There is intentionally no public constructor or arbitrary-target factory.
/// </summary>
public sealed class ApprovedPuzzle
{
    private readonly byte[] _targetHash160;

    internal ApprovedPuzzle(
        string id,
        string name,
        ApprovedTargetKind kind,
        string network,
        string status,
        UInt256 keyRangeStart,
        UInt256 keyRangeEnd,
        byte[] targetHash160,
        string source,
        string? publicAddress = null)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Network = network;
        Status = status;
        KeyRangeStart = keyRangeStart;
        KeyRangeEnd = keyRangeEnd;
        _targetHash160 = targetHash160;
        Source = source;
        PublicAddress = publicAddress;
        ManifestFingerprint = ComputeFingerprint();
    }

    public string Id { get; }
    public string Name { get; }
    public ApprovedTargetKind Kind { get; }
    public string Network { get; }
    public string Status { get; }
    public UInt256 KeyRangeStart { get; }
    public UInt256 KeyRangeEnd { get; }
    public ReadOnlyMemory<byte> TargetHash160 => _targetHash160.ToArray();
    internal ReadOnlySpan<byte> TargetHash160Span => _targetHash160;
    public string Source { get; }
    public string? PublicAddress { get; }
    public string ManifestFingerprint { get; }

    private string ComputeFingerprint()
    {
        var canonical = string.Join('\n',
            Id,
            Name,
            Kind.ToString(),
            Network,
            Status,
            KeyRangeStart.ToString(),
            KeyRangeEnd.ToString(),
            Convert.ToHexString(_targetHash160).ToLowerInvariant(),
            Source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
