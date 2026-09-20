using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TrPuzzle.Core;

namespace TrPuzzle.Engine;

public static class EncryptedResultVault
{
    private static readonly byte[] Magic = "TRPVLT01"u8.ToArray();
    private const int Iterations = 600_000;

    public sealed class DecryptedRecord : IDisposable
    {
        private byte[]? _privateKey;

        internal DecryptedRecord(string puzzleId, DateTimeOffset createdAt, byte[] privateKey)
        {
            PuzzleId = puzzleId;
            CreatedAt = createdAt;
            _privateKey = privateKey;
        }

        public string PuzzleId { get; }
        public DateTimeOffset CreatedAt { get; }
        public UInt256 Candidate
        {
            get
            {
                var key = _privateKey ?? throw new ObjectDisposedException(nameof(DecryptedRecord));
                return UInt256.FromBigEndian(key);
            }
        }

        public string CandidateFingerprint
        {
            get
            {
                var key = _privateKey ?? throw new ObjectDisposedException(nameof(DecryptedRecord));
                return Convert.ToHexString(SHA256.HashData(key).AsSpan(0, 8)).ToLowerInvariant();
            }
        }

        public string PrivateKeyHex =>
            Convert.ToHexString(_privateKey ?? throw new ObjectDisposedException(nameof(DecryptedRecord))).ToLowerInvariant();

        public void Dispose()
        {
            var key = Interlocked.Exchange(ref _privateKey, null);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            GC.SuppressFinalize(this);
        }

        ~DecryptedRecord() => Dispose();
    }

    public static void WriteNew(string path, ApprovedPuzzle puzzle, SearchResult result, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(puzzle);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Found) throw new InvalidOperationException("Cannot create a result vault without a verified candidate.");
        if (password.Length < 12) throw new ArgumentException("Vault password must be at least 12 characters.", nameof(password));

        var puzzleIdBytes = Encoding.UTF8.GetBytes(puzzle.Id);
        if (puzzleIdBytes.Length > ushort.MaxValue) throw new InvalidOperationException("Puzzle id is too long for the vault format.");
        var payload = new byte[1 + 2 + puzzleIdBytes.Length + 8 + 32];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), checked((ushort)puzzleIdBytes.Length));
        puzzleIdBytes.CopyTo(payload, 3);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(3 + puzzleIdBytes.Length), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        result.DangerousPrivateKeySpan.CopyTo(payload.AsSpan(3 + puzzleIdBytes.Length + 8));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[payload.Length];
        var passwordBytes = new byte[Encoding.UTF8.GetByteCount(password)];
        Encoding.UTF8.GetBytes(password, passwordBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, Iterations, HashAlgorithmName.SHA256, 32);

        try
        {
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, payload, ciphertext, tag, Encoding.UTF8.GetBytes(puzzle.Id));
            }

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Vault path has no parent directory.");
            Directory.CreateDirectory(directory);
            if (File.Exists(fullPath)) throw new IOException("Refusing to overwrite an existing result vault.");

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(Magic);
                    Span<byte> header = stackalloc byte[8];
                    BinaryPrimitives.WriteInt32LittleEndian(header, Iterations);
                    BinaryPrimitives.WriteInt32LittleEndian(header[4..], ciphertext.Length);
                    stream.Write(header);
                    stream.Write(salt);
                    stream.Write(nonce);
                    stream.Write(tag);
                    stream.Write(ciphertext);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, fullPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(puzzleIdBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static DecryptedRecord Read(string path, ApprovedPuzzle puzzle, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(puzzle);
        if (password.Length < 12) throw new ArgumentException("Vault password must be at least 12 characters.", nameof(password));

        var bytes = File.ReadAllBytes(Path.GetFullPath(path));
        byte[]? payload = null;
        byte[]? passwordBytes = null;
        byte[]? key = null;
        byte[]? aad = null;
        try
        {
            if (bytes.Length < 60 || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new InvalidDataException("Vault header is invalid.");
            }

            var header = bytes.AsSpan(Magic.Length, 8);
            var iterations = BinaryPrimitives.ReadInt32LittleEndian(header);
            var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if (iterations <= 0 || iterations > 10_000_000 || ciphertextLength <= 0 || ciphertextLength > 1_048_576)
            {
                throw new InvalidDataException("Vault header values are invalid.");
            }

            var expectedLength = checked(60 + ciphertextLength);
            if (bytes.Length != expectedLength) throw new InvalidDataException("Vault length is invalid.");

            var salt = bytes.AsSpan(16, 16);
            var nonce = bytes.AsSpan(32, 12);
            var tag = bytes.AsSpan(44, 16);
            var ciphertext = bytes.AsSpan(60, ciphertextLength);
            payload = new byte[ciphertextLength];
            passwordBytes = new byte[Encoding.UTF8.GetByteCount(password)];
            Encoding.UTF8.GetBytes(password, passwordBytes);
            key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, 32);
            aad = Encoding.UTF8.GetBytes(puzzle.Id);
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Decrypt(nonce, ciphertext, tag, payload, aad);
            }

            if (payload.Length < 1 + 2 + 8 + 32 || payload[0] != 1)
            {
                throw new InvalidDataException("Vault payload version is unsupported.");
            }

            var puzzleIdLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1, 2));
            var payloadIdEnd = checked(3 + puzzleIdLength);
            var payloadLength = checked(payloadIdEnd + 8 + 32);
            if (payloadLength != payload.Length) throw new InvalidDataException("Vault payload length is invalid.");
            var payloadPuzzleId = Encoding.UTF8.GetString(payload, 3, puzzleIdLength);
            if (!string.Equals(payloadPuzzleId, puzzle.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Vault belongs to a different approved puzzle.");
            }

            var createdAtSeconds = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(payloadIdEnd, 8));
            var candidateBytes = payload.AsSpan(payloadIdEnd + 8, 32).ToArray();
            var candidate = UInt256.FromBigEndian(candidateBytes);
            if (!CandidateVerifier.Verify(puzzle, candidate))
            {
                CryptographicOperations.ZeroMemory(candidateBytes);
                throw new InvalidDataException("Vault candidate failed independent CPU verification.");
            }

            return new DecryptedRecord(
                puzzle.Id,
                DateTimeOffset.FromUnixTimeSeconds(createdAtSeconds),
                candidateBytes);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Vault password is incorrect or the vault is damaged.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (aad is not null) CryptographicOperations.ZeroMemory(aad);
        }
    }
}
