using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrPuzzle.Core;

public sealed class ApprovedPuzzleCatalog
{
    private const string ManifestType = "trpuzzle-approved-targets";
    private static readonly string[] ResourceNames =
    [
        "TrPuzzle.Manifests.public-btc-puzzles.json",
        "TrPuzzle.Manifests.test-vectors.json"
    ];

    private readonly IReadOnlyDictionary<string, ApprovedPuzzle> _byId;

    private ApprovedPuzzleCatalog(IReadOnlyDictionary<string, ApprovedPuzzle> byId) => _byId = byId;

    public IReadOnlyCollection<ApprovedPuzzle> All => _byId.Values.ToArray();

    public static ApprovedPuzzleCatalog LoadEmbedded()
    {
        var assembly = typeof(ApprovedPuzzleCatalog).Assembly;
        var puzzles = new Dictionary<string, ApprovedPuzzle>(StringComparer.Ordinal);
        foreach (var resourceName in ResourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Required embedded manifest is missing: {resourceName}");
            ReadManifest(stream, puzzles, resourceName);
        }

        return new ApprovedPuzzleCatalog(puzzles);
    }

    public ApprovedPuzzle GetRequired(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _byId.TryGetValue(id, out var puzzle)
            ? puzzle
            : throw new KeyNotFoundException($"Target '{id}' is not present in the embedded approved manifest.");
    }

    public bool TryGet(string id, out ApprovedPuzzle? puzzle) => _byId.TryGetValue(id, out puzzle);

    private static void ReadManifest(Stream stream, IDictionary<string, ApprovedPuzzle> puzzles, string resourceName)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });

        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("manifestType").GetString() != ManifestType)
        {
            throw new InvalidDataException($"Unsupported manifest header in {resourceName}.");
        }

        foreach (var item in root.GetProperty("targets").EnumerateArray())
        {
            var id = RequiredString(item, "id");
            var name = RequiredString(item, "name");
            var kind = RequiredString(item, "kind") switch
            {
                "public-puzzle" => ApprovedTargetKind.PublicPuzzle,
                "test-vector" => ApprovedTargetKind.TestVector,
                _ => throw new InvalidDataException($"Target '{id}' has an unsupported kind.")
            };
            var network = RequiredString(item, "network");
            if (network != "bitcoin-mainnet")
            {
                throw new InvalidDataException($"Target '{id}' is not a Bitcoin mainnet target.");
            }

            var start = ParseManifestUInt256(item, "keyRangeStart", id);
            var end = ParseManifestUInt256(item, "keyRangeEnd", id);
            if (start == UInt256.Zero || start > end)
            {
                throw new InvalidDataException($"Target '{id}' has an invalid key range.");
            }

            var targetHex = RequiredString(item, "targetHash160");
            if (targetHex.Length != 40 || targetHex.Any(static c => !Uri.IsHexDigit(c)))
            {
                throw new InvalidDataException($"Target '{id}' must contain exactly 20 bytes of HASH160.");
            }

            string? publicAddress = null;
            if (item.TryGetProperty("address", out var addressElement) && addressElement.ValueKind == JsonValueKind.String)
            {
                publicAddress = addressElement.GetString();
                if (string.IsNullOrWhiteSpace(publicAddress)) publicAddress = null;
            }
            if (kind == ApprovedTargetKind.PublicPuzzle && publicAddress is null)
            {
                throw new InvalidDataException($"Public target '{id}' must contain its published address.");
            }
            if (publicAddress is not null &&
                (!BitcoinAddress.TryDecodeMainnetP2pkh(publicAddress, out var addressHash160) ||
                 !CryptographicOperations.FixedTimeEquals(addressHash160, Convert.FromHexString(targetHex))))
            {
                throw new InvalidDataException($"Target '{id}' address does not match its embedded HASH160.");
            }

            var puzzle = new ApprovedPuzzle(
                id,
                name,
                kind,
                network,
                RequiredString(item, "status"),
                start,
                end,
                Convert.FromHexString(targetHex),
                RequiredString(item, "source"),
                publicAddress);

            if (!puzzles.TryAdd(id, puzzle))
            {
                throw new InvalidDataException($"Duplicate approved target id: {id}");
            }
        }
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Manifest field '{propertyName}' is required.")
            : value;
    }

    private static UInt256 ParseManifestUInt256(JsonElement element, string propertyName, string id)
    {
        var value = RequiredString(element, propertyName);
        if (value.Length != 64 || value.Any(static c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException($"Target '{id}' field '{propertyName}' must contain exactly 32 bytes of hexadecimal data.");
        }

        return UInt256.Parse(value);
    }
}
