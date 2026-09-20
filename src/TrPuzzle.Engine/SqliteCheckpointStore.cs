using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;
using TrPuzzle.Core;

namespace TrPuzzle.Engine;

public sealed class SqliteCheckpointStore : ICheckpointStore
{
    private const int SchemaVersion = 2;
    private readonly string _connectionString;

    public SqliteCheckpointStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Checkpoint database needs a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaMetadata (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                version INTEGER NOT NULL
            ) STRICT;

            INSERT OR IGNORE INTO SchemaMetadata(singleton, version) VALUES (1, 2);

            CREATE TABLE IF NOT EXISTS SearchSessions (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                puzzle_id TEXT NOT NULL UNIQUE,
                manifest_fingerprint TEXT NOT NULL CHECK (length(manifest_fingerprint) = 64),
                range_start TEXT NOT NULL CHECK (length(range_start) = 64),
                range_end TEXT NOT NULL CHECK (length(range_end) = 64),
                keys_per_package INTEGER NOT NULL CHECK (keys_per_package > 0),
                next_sequence INTEGER NOT NULL DEFAULT 0 CHECK (next_sequence >= 0),
                next_unassigned_key TEXT NULL CHECK (next_unassigned_key IS NULL OR length(next_unassigned_key) = 64),
                current_epoch INTEGER NOT NULL DEFAULT 0 CHECK (current_epoch >= 0),
                schedule_seed TEXT NOT NULL DEFAULT '' CHECK (length(schedule_seed) IN (0, 64)),
                next_chunk_cursor TEXT NOT NULL DEFAULT '0',
                epoch_chunk_count TEXT NOT NULL DEFAULT '0',
                status TEXT NOT NULL CHECK (status IN ('Running', 'Paused', 'Completed', 'Found', 'Stopped')),
                created_utc INTEGER NOT NULL,
                updated_utc INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS WorkPackages (
                session_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence >= 0),
                range_start TEXT NOT NULL CHECK (length(range_start) = 64),
                range_end TEXT NOT NULL CHECK (length(range_end) = 64),
                next_key TEXT NOT NULL CHECK (length(next_key) = 64),
                status TEXT NOT NULL CHECK (status IN ('Pending', 'Leased', 'Completed')),
                lease_owner TEXT NULL,
                lease_expires_utc INTEGER NULL,
                checked_keys INTEGER NOT NULL DEFAULT 0 CHECK (checked_keys >= 0),
                keys_per_second REAL NOT NULL DEFAULT 0 CHECK (keys_per_second >= 0),
                epoch INTEGER NOT NULL DEFAULT 0 CHECK (epoch >= 0),
                chunk_index TEXT NOT NULL DEFAULT '0',
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                checkpoint_utc INTEGER NOT NULL,
                PRIMARY KEY (session_id, sequence),
                FOREIGN KEY (session_id) REFERENCES SearchSessions(id) ON DELETE RESTRICT
            ) STRICT;

            CREATE INDEX IF NOT EXISTS IX_WorkPackages_Lease
                ON WorkPackages(session_id, status, sequence);

            CREATE TABLE IF NOT EXISTS AuditEvents (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL,
                package_sequence INTEGER NULL,
                event_type TEXT NOT NULL,
                worker_id TEXT NULL,
                detail TEXT NOT NULL,
                created_utc INTEGER NOT NULL,
                FOREIGN KEY (session_id) REFERENCES SearchSessions(id) ON DELETE RESTRICT
            ) STRICT;

            CREATE TABLE IF NOT EXISTS ScanProfiles (
                puzzle_id TEXT NOT NULL PRIMARY KEY,
                manifest_fingerprint TEXT NOT NULL CHECK (length(manifest_fingerprint) = 64),
                max_hex_run INTEGER NOT NULL CHECK (max_hex_run BETWEEN 0 AND 63)
            ) STRICT;
            """;
        command.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT version FROM SchemaMetadata WHERE singleton = 1;";
        var actualVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (actualVersion == 1)
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = """
                ALTER TABLE SearchSessions ADD COLUMN current_epoch INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE SearchSessions ADD COLUMN schedule_seed TEXT NOT NULL DEFAULT '';
                ALTER TABLE SearchSessions ADD COLUMN next_chunk_cursor TEXT NOT NULL DEFAULT '0';
                ALTER TABLE SearchSessions ADD COLUMN epoch_chunk_count TEXT NOT NULL DEFAULT '0';
                ALTER TABLE WorkPackages ADD COLUMN epoch INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE WorkPackages ADD COLUMN chunk_index TEXT NOT NULL DEFAULT '0';
                ALTER TABLE WorkPackages ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0;
                UPDATE WorkPackages SET chunk_index = CAST(sequence AS TEXT);
                UPDATE SchemaMetadata SET version = 2 WHERE singleton = 1;
                """;
            migration.ExecuteNonQuery();
            actualVersion = SchemaVersion;
        }

        if (actualVersion != SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported checkpoint schema version {actualVersion}.");
        }
    }

    public void EnsureScanProfile(ApprovedPuzzle puzzle, int maxHexRun)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        if (maxHexRun is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(maxHexRun));

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var select = CreateCommand(connection, transaction, """
            SELECT manifest_fingerprint, max_hex_run
            FROM ScanProfiles WHERE puzzle_id = $puzzle_id;
            """))
        {
            select.Parameters.AddWithValue("$puzzle_id", puzzle.Id);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                if (!string.Equals(reader.GetString(0), puzzle.ManifestFingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Checkpoint scan profile manifest fingerprint does not match the approved puzzle.");
                }

                if (reader.GetInt32(1) != maxHexRun)
                {
                    throw new InvalidOperationException("Existing session uses a different hexadecimal run filter.");
                }

                reader.Close();
                transaction.Commit();
                return;
            }
        }

        using (var legacy = CreateCommand(connection, transaction, """
            SELECT COUNT(*) FROM SearchSessions WHERE puzzle_id = $puzzle_id;
            """))
        {
            legacy.Parameters.AddWithValue("$puzzle_id", puzzle.Id);
            var hasExistingSession = Convert.ToInt64(legacy.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
            if (hasExistingSession && maxHexRun != 0)
            {
                throw new InvalidOperationException("Existing legacy session is unfiltered; use a new checkpoint database for a filtered scan.");
            }
        }

        using (var insert = CreateCommand(connection, transaction, """
            INSERT INTO ScanProfiles(puzzle_id, manifest_fingerprint, max_hex_run)
            VALUES($puzzle_id, $fingerprint, $max_hex_run);
            """))
        {
            insert.Parameters.AddWithValue("$puzzle_id", puzzle.Id);
            insert.Parameters.AddWithValue("$fingerprint", puzzle.ManifestFingerprint);
            insert.Parameters.AddWithValue("$max_hex_run", maxHexRun);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public long CreateOrOpenSession(ApprovedPuzzle puzzle, ulong keysPerPackage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        if (keysPerPackage is 0 or > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(keysPerPackage));

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var select = CreateCommand(connection, transaction, """
            SELECT id, manifest_fingerprint, range_start, range_end, keys_per_package
            FROM SearchSessions WHERE puzzle_id = $puzzle_id;
            """))
        {
            select.Parameters.AddWithValue("$puzzle_id", puzzle.Id);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                var sessionId = reader.GetInt64(0);
                ValidatePersistedIdentity(puzzle, reader.GetString(1), reader.GetString(2), reader.GetString(3));
                if (reader.GetInt64(4) != checked((long)keysPerPackage))
                {
                    throw new InvalidOperationException("Existing session uses a different package size.");
                }

                reader.Close();
                transaction.Commit();
                return sessionId;
            }
        }

        var seed = RangePlanner.CreateSeed();
        var chunkCount = RangePlanner.GetChunkCount(puzzle, keysPerPackage);
        if (chunkCount > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(keysPerPackage), "The approved range contains more chunks than the SQLite sequence model can address.");
        }

        long id;
        using (var insert = CreateCommand(connection, transaction, """
            INSERT INTO SearchSessions(
                puzzle_id, manifest_fingerprint, range_start, range_end, keys_per_package,
                next_sequence, next_unassigned_key, current_epoch, schedule_seed,
                next_chunk_cursor, epoch_chunk_count, status, created_utc, updated_utc)
            VALUES(
                $puzzle_id, $fingerprint, $range_start, $range_end, $package_size,
                0, NULL, 0, $seed, '0', $chunk_count, 'Running', $now, $now)
            RETURNING id;
            """))
        {
            insert.Parameters.AddWithValue("$puzzle_id", puzzle.Id);
            insert.Parameters.AddWithValue("$fingerprint", puzzle.ManifestFingerprint);
            insert.Parameters.AddWithValue("$range_start", puzzle.KeyRangeStart.ToString());
            insert.Parameters.AddWithValue("$range_end", puzzle.KeyRangeEnd.ToString());
            insert.Parameters.AddWithValue("$package_size", checked((long)keysPerPackage));
            insert.Parameters.AddWithValue("$seed", RangePlanner.SeedToHex(seed));
            insert.Parameters.AddWithValue("$chunk_count", chunkCount.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        InsertAudit(connection, transaction, id, null, "SessionCreated", null, $"package-size={keysPerPackage}", now);
        transaction.Commit();
        return id;
    }

    public long? GetSessionId(ApprovedPuzzle puzzle)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, manifest_fingerprint, range_start, range_end FROM SearchSessions WHERE puzzle_id = $puzzle_id;";
        command.Parameters.AddWithValue("$puzzle_id", puzzle.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        ValidatePersistedIdentity(puzzle, reader.GetString(1), reader.GetString(2), reader.GetString(3));
        return reader.GetInt64(0);
    }

    public WorkPackageLease? TryLeaseNext(
        ApprovedPuzzle puzzle,
        long sessionId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        ValidateWorkerAndLease(workerId, leaseDuration);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadAndValidateSession(connection, transaction, puzzle, sessionId);
        if (session.State != SearchSessionState.Running)
        {
            transaction.Commit();
            return null;
        }

        using (var recover = CreateCommand(connection, transaction, """
            UPDATE WorkPackages
            SET status = 'Pending', lease_owner = NULL, lease_expires_utc = NULL
            WHERE session_id = $session_id AND epoch = $epoch
              AND status = 'Leased' AND lease_expires_utc <= $now;
            """))
        {
            recover.Parameters.AddWithValue("$session_id", sessionId);
            recover.Parameters.AddWithValue("$epoch", session.CurrentEpoch);
            recover.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            var recovered = recover.ExecuteNonQuery();
            if (recovered > 0)
            {
                InsertAudit(connection, transaction, sessionId, null, "ExpiredLeasesRecovered", null, $"count={recovered}", now);
            }
        }

        var package = ReadPendingPackage(connection, transaction, sessionId, puzzle.Id, session.CurrentEpoch);
        if (package is null && HasUnassignedChunk(session))
        {
            package = AllocatePackage(connection, transaction, puzzle, sessionId, session, now);
        }

        if (package is null)
        {
            transaction.Commit();
            return null;
        }

        var expires = now.Add(leaseDuration);
        using (var lease = CreateCommand(connection, transaction, """
            UPDATE WorkPackages
            SET status = 'Leased', lease_owner = $worker_id, lease_expires_utc = $expires,
                attempt_count = attempt_count + 1,
                checkpoint_utc = $now
            WHERE session_id = $session_id AND sequence = $sequence AND epoch = $epoch AND status = 'Pending';
            """))
        {
            lease.Parameters.AddWithValue("$worker_id", workerId);
            lease.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expires));
            lease.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            lease.Parameters.AddWithValue("$session_id", sessionId);
            lease.Parameters.AddWithValue("$sequence", package.Sequence);
            lease.Parameters.AddWithValue("$epoch", session.CurrentEpoch);
            if (lease.ExecuteNonQuery() != 1) throw new InvalidOperationException("Work package could not be leased atomically.");
        }

        InsertAudit(connection, transaction, sessionId, package.Sequence, "PackageLeased", workerId, "lease-acquired", now);
        transaction.Commit();
        return new WorkPackageLease(sessionId, package.Package, package.NextKey, workerId, expires, package.CheckedKeys);
    }

    public WorkPackageLease SaveCheckpoint(
        ApprovedPuzzle puzzle,
        WorkPackageLease lease,
        UInt256 nextKey,
        ulong checkedKeys,
        double keysPerSecond,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateWorkerAndLease(lease.WorkerId, leaseDuration);
        ValidateProgress(lease, nextKey, checkedKeys, keysPerSecond);
        var expires = now.Add(leaseDuration);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadAndValidateSession(connection, transaction, puzzle, lease.SessionId);
        if (session.State != SearchSessionState.Running)
        {
            throw new OperationCanceledException($"Session is {session.State}; checkpoint was not written.");
        }
        using (var update = CreateCommand(connection, transaction, """
            UPDATE WorkPackages
            SET next_key = $next_key, checked_keys = $checked_keys, keys_per_second = $speed,
                checkpoint_utc = $now, lease_expires_utc = $expires
            WHERE session_id = $session_id AND sequence = $sequence
              AND status = 'Leased' AND lease_owner = $worker_id
              AND next_key <= $next_key AND checked_keys <= $checked_keys;
            """))
        {
            update.Parameters.AddWithValue("$next_key", nextKey.ToString());
            update.Parameters.AddWithValue("$checked_keys", checked((long)checkedKeys));
            update.Parameters.AddWithValue("$speed", keysPerSecond);
            update.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            update.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expires));
            update.Parameters.AddWithValue("$session_id", lease.SessionId);
            update.Parameters.AddWithValue("$sequence", lease.Package.Sequence);
            update.Parameters.AddWithValue("$worker_id", lease.WorkerId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Checkpoint rejected: lease is stale or progress moved backwards.");
            }
        }

        InsertAudit(connection, transaction, lease.SessionId, lease.Package.Sequence, "CheckpointSaved", lease.WorkerId, $"checked={checkedKeys}", now);
        transaction.Commit();
        return lease with { NextKey = nextKey, CheckedKeys = checkedKeys, LeaseExpiresAt = expires };
    }

    public void CompletePackage(
        ApprovedPuzzle puzzle,
        WorkPackageLease lease,
        ulong checkedKeys,
        double keysPerSecond,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateCounterAndSpeed(checkedKeys, keysPerSecond);
        ValidatePackageCheckedCount(lease, checkedKeys);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadAndValidateSession(connection, transaction, puzzle, lease.SessionId);
        using (var update = CreateCommand(connection, transaction, """
            UPDATE WorkPackages
            SET status = 'Completed', next_key = range_end, checked_keys = $checked_keys,
                keys_per_second = $speed, checkpoint_utc = $now,
                lease_owner = NULL, lease_expires_utc = NULL
            WHERE session_id = $session_id AND sequence = $sequence
              AND status = 'Leased' AND lease_owner = $worker_id
              AND checked_keys <= $checked_keys;
            """))
        {
            update.Parameters.AddWithValue("$checked_keys", checked((long)checkedKeys));
            update.Parameters.AddWithValue("$speed", keysPerSecond);
            update.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            update.Parameters.AddWithValue("$session_id", lease.SessionId);
            update.Parameters.AddWithValue("$sequence", lease.Package.Sequence);
            update.Parameters.AddWithValue("$worker_id", lease.WorkerId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Package completion rejected because its lease is stale.");
        }

        InsertAudit(connection, transaction, lease.SessionId, lease.Package.Sequence, "PackageCompleted", lease.WorkerId, $"checked={checkedKeys}", now);
        if (!HasUnassignedChunk(session) && CountOpenPackages(connection, transaction, lease.SessionId, session.CurrentEpoch) == 0)
        {
            SetSessionState(connection, transaction, lease.SessionId, SearchSessionState.Completed, now);
            InsertAudit(connection, transaction, lease.SessionId, null, "SessionCompleted", null, "all-packages-completed", now);
        }

        transaction.Commit();
    }

    public void ReleaseLease(ApprovedPuzzle puzzle, WorkPackageLease lease, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        ReadAndValidateSession(connection, transaction, puzzle, lease.SessionId);
        using (var update = CreateCommand(connection, transaction, """
            UPDATE WorkPackages SET status = 'Pending', lease_owner = NULL, lease_expires_utc = NULL,
                checkpoint_utc = $now
            WHERE session_id = $session_id AND sequence = $sequence
              AND status = 'Leased' AND lease_owner = $worker_id;
            """))
        {
            update.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            update.Parameters.AddWithValue("$session_id", lease.SessionId);
            update.Parameters.AddWithValue("$sequence", lease.Package.Sequence);
            update.Parameters.AddWithValue("$worker_id", lease.WorkerId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Lease release rejected because its lease is stale.");
        }

        InsertAudit(connection, transaction, lease.SessionId, lease.Package.Sequence, "LeaseReleased", lease.WorkerId, reason, now);
        transaction.Commit();
    }

    public void SetPaused(ApprovedPuzzle puzzle, long sessionId, bool paused, DateTimeOffset now)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadAndValidateSession(connection, transaction, puzzle, sessionId);
        var desired = paused ? SearchSessionState.Paused : SearchSessionState.Running;
        if (session.State is SearchSessionState.Completed or SearchSessionState.Found or SearchSessionState.Stopped)
        {
            throw new InvalidOperationException($"Session in state {session.State} cannot be paused or resumed.");
        }

        SetSessionState(connection, transaction, sessionId, desired, now);
        InsertAudit(connection, transaction, sessionId, null, paused ? "SessionPaused" : "SessionResumed", null, "operator-request", now);
        transaction.Commit();
    }

    public SearchSessionSnapshot StartNewEpoch(ApprovedPuzzle puzzle, long sessionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadAndValidateSession(connection, transaction, puzzle, sessionId);
        if (session.State != SearchSessionState.Completed)
        {
            throw new InvalidOperationException($"Only a completed session can start a new verification epoch; current state is {session.State}.");
        }

        if (session.CurrentEpoch == long.MaxValue)
        {
            throw new InvalidOperationException("Epoch counter reached its maximum value.");
        }

        var seed = RangePlanner.CreateSeed();
        var chunkCount = RangePlanner.GetChunkCount(puzzle, checked((ulong)session.KeysPerPackage));
        if (chunkCount > long.MaxValue)
        {
            throw new InvalidOperationException("The approved range contains more chunks than the SQLite sequence model can address.");
        }

        using (var update = CreateCommand(connection, transaction, """
            UPDATE SearchSessions
            SET current_epoch = $epoch, schedule_seed = $seed,
                next_chunk_cursor = '0', epoch_chunk_count = $chunk_count,
                next_unassigned_key = NULL, status = 'Running', updated_utc = $now
            WHERE id = $session_id;
            """))
        {
            update.Parameters.AddWithValue("$epoch", checked(session.CurrentEpoch + 1));
            update.Parameters.AddWithValue("$seed", RangePlanner.SeedToHex(seed));
            update.Parameters.AddWithValue("$chunk_count", chunkCount.ToString(CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            update.Parameters.AddWithValue("$session_id", sessionId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Epoch transition failed.");
        }

        InsertAudit(connection, transaction, sessionId, null, "EpochStarted", null,
            $"epoch={session.CurrentEpoch + 1};chunks={chunkCount}", now);
        transaction.Commit();
        return GetSnapshot(puzzle, sessionId);
    }

    public void MarkFound(
        ApprovedPuzzle puzzle,
        WorkPackageLease lease,
        ulong checkedKeys,
        double keysPerSecond,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateCounterAndSpeed(checkedKeys, keysPerSecond);
        ValidatePackageCheckedCount(lease, checkedKeys);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadAndValidateSession(connection, transaction, puzzle, lease.SessionId);
        if (session.State is SearchSessionState.Completed or SearchSessionState.Stopped)
        {
            throw new InvalidOperationException($"Session in state {session.State} cannot be marked found.");
        }

        using (var update = CreateCommand(connection, transaction, """
            UPDATE WorkPackages
            SET status = 'Completed', next_key = range_end, checked_keys = $checked_keys,
                keys_per_second = $speed, checkpoint_utc = $now,
                lease_owner = NULL, lease_expires_utc = NULL
            WHERE session_id = $session_id AND sequence = $sequence
              AND status = 'Leased' AND lease_owner = $worker_id;
            """))
        {
            update.Parameters.AddWithValue("$checked_keys", checked((long)checkedKeys));
            update.Parameters.AddWithValue("$speed", keysPerSecond);
            update.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            update.Parameters.AddWithValue("$session_id", lease.SessionId);
            update.Parameters.AddWithValue("$sequence", lease.Package.Sequence);
            update.Parameters.AddWithValue("$worker_id", lease.WorkerId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Found package could not be closed because its lease is stale.");
        }

        SetSessionState(connection, transaction, lease.SessionId, SearchSessionState.Found, now);
        InsertAudit(connection, transaction, lease.SessionId, lease.Package.Sequence, "CandidateVerified", lease.WorkerId, $"checked={checkedKeys}", now);
        InsertAudit(connection, transaction, lease.SessionId, null, "SessionFound", null, "all-workers-must-stop", now);
        transaction.Commit();
    }

    public SearchSessionSnapshot GetSnapshot(ApprovedPuzzle puzzle, long sessionId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var session = ReadAndValidateSession(connection, transaction, puzzle, sessionId);
        long pending = 0; long leased = 0; long completed = 0; long checkedKeys = 0;
        using (var command = CreateCommand(connection, transaction, """
            SELECT status, COUNT(*), COALESCE(SUM(checked_keys), 0)
            FROM WorkPackages WHERE session_id = $session_id AND epoch = $epoch GROUP BY status;
            """))
        {
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$epoch", session.CurrentEpoch);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var count = reader.GetInt64(1);
                checkedKeys = checked(checkedKeys + reader.GetInt64(2));
                switch (reader.GetString(0))
                {
                    case "Pending": pending = count; break;
                    case "Leased": leased = count; break;
                    case "Completed": completed = count; break;
                    default: throw new InvalidDataException("Checkpoint contains an unknown package state.");
                }
            }
        }

        transaction.Commit();
        return new SearchSessionSnapshot(
            sessionId,
            puzzle.Id,
            session.CurrentEpoch,
            session.State,
            pending,
            leased,
            completed,
            checked((ulong)checkedKeys),
            session.NextUnassignedKey);
    }

    private SessionRow ReadAndValidateSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApprovedPuzzle puzzle,
        long sessionId)
    {
        ArgumentNullException.ThrowIfNull(puzzle);
        using var command = CreateCommand(connection, transaction, """
            SELECT puzzle_id, manifest_fingerprint, range_start, range_end, keys_per_package,
                   next_sequence, next_unassigned_key, current_epoch, schedule_seed,
                   next_chunk_cursor, epoch_chunk_count, status
            FROM SearchSessions WHERE id = $session_id;
            """);
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Checkpoint session {sessionId} does not exist.");
        if (!string.Equals(reader.GetString(0), puzzle.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Checkpoint belongs to a different approved puzzle.");
        }

        ValidatePersistedIdentity(puzzle, reader.GetString(1), reader.GetString(2), reader.GetString(3));
        return new SessionRow(
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : UInt256.Parse(reader.GetString(6)),
            reader.GetInt64(7),
            reader.GetString(8),
            ParseBigInteger(reader.GetString(9), "next_chunk_cursor"),
            ParseBigInteger(reader.GetString(10), "epoch_chunk_count"),
            ParseSessionState(reader.GetString(11)));
    }

    private static PackageRow? ReadPendingPackage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sessionId,
        string puzzleId,
        long epoch)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT sequence, range_start, range_end, next_key, checked_keys
            FROM WorkPackages
            WHERE session_id = $session_id AND epoch = $epoch AND status = 'Pending'
            ORDER BY sequence LIMIT 1;
            """);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$epoch", epoch);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return CreatePackageRow(puzzleId, reader);
    }

    private static PackageRow AllocatePackage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApprovedPuzzle puzzle,
        long sessionId,
        SessionRow session,
        DateTimeOffset now)
    {
        var packageSize = checked((ulong)session.KeysPerPackage);
        var randomized = !string.IsNullOrEmpty(session.ScheduleSeed);
        var chunkIndex = randomized
            ? RangePlanner.PermuteChunkIndex(
                RangePlanner.SeedFromHex(session.ScheduleSeed),
                session.NextChunkCursor,
                session.EpochChunkCount)
            : session.NextSequence;
        var range = randomized
            ? RangePlanner.GetChunkRange(puzzle, packageSize, chunkIndex)
            : GetSequentialRange(puzzle, packageSize, session.NextUnassignedKey
                ?? throw new InvalidOperationException("Sequential session has no unassigned key."));
        var start = range.Start;
        var end = range.End;
        var package = new WorkPackage(puzzle.Id, session.NextSequence, range);
        using (var insert = CreateCommand(connection, transaction, """
            INSERT INTO WorkPackages(
                session_id, sequence, range_start, range_end, next_key, status,
                lease_owner, lease_expires_utc, checked_keys, keys_per_second,
                epoch, chunk_index, attempt_count, checkpoint_utc)
            VALUES($session_id, $sequence, $start, $end, $start, 'Pending', NULL, NULL, 0, 0,
                   $epoch, $chunk_index, 0, $now);
            """))
        {
            insert.Parameters.AddWithValue("$session_id", sessionId);
            insert.Parameters.AddWithValue("$sequence", package.Sequence);
            insert.Parameters.AddWithValue("$start", range.Start.ToString());
            insert.Parameters.AddWithValue("$end", range.End.ToString());
            insert.Parameters.AddWithValue("$epoch", session.CurrentEpoch);
            insert.Parameters.AddWithValue("$chunk_index", chunkIndex.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            insert.ExecuteNonQuery();
        }

        string? nextUnassigned = null;
        if (!randomized && end != puzzle.KeyRangeEnd)
        {
            if (!end.TryIncrement(out var next)) throw new InvalidOperationException("Package allocation overflowed.");
            nextUnassigned = next.ToString();
        }

        using (var update = CreateCommand(connection, transaction, """
            UPDATE SearchSessions
            SET next_sequence = $next_sequence, next_unassigned_key = $next_key,
                next_chunk_cursor = $next_cursor, updated_utc = $now
            WHERE id = $session_id;
            """))
        {
            update.Parameters.AddWithValue("$next_sequence", checked(package.Sequence + 1));
            update.Parameters.AddWithValue("$next_key", nextUnassigned is null ? DBNull.Value : nextUnassigned);
            update.Parameters.AddWithValue("$next_cursor", randomized
                ? (session.NextChunkCursor + BigInteger.One).ToString(CultureInfo.InvariantCulture)
                : session.NextChunkCursor.ToString(CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            update.Parameters.AddWithValue("$session_id", sessionId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Session allocation cursor update failed.");
        }

        return new PackageRow(package, start, 0);
    }

    private static WorkRange GetSequentialRange(ApprovedPuzzle puzzle, ulong packageSize, UInt256 start)
    {
        var hasEnd = start.TryAdd(packageSize - 1, out var candidateEnd);
        var end = !hasEnd || candidateEnd > puzzle.KeyRangeEnd ? puzzle.KeyRangeEnd : candidateEnd;
        return new WorkRange(start, end);
    }

    private static PackageRow CreatePackageRow(string puzzleId, SqliteDataReader reader)
    {
        var sequence = reader.GetInt64(0);
        var range = new WorkRange(UInt256.Parse(reader.GetString(1)), UInt256.Parse(reader.GetString(2)));
        var package = new WorkPackage(puzzleId, sequence, range);
        return new PackageRow(package, UInt256.Parse(reader.GetString(3)), checked((ulong)reader.GetInt64(4)));
    }

    private static int CountOpenPackages(SqliteConnection connection, SqliteTransaction transaction, long sessionId, long epoch)
    {
        using var command = CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM WorkPackages WHERE session_id = $session_id AND epoch = $epoch AND status != 'Completed';");
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$epoch", epoch);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool HasUnassignedChunk(SessionRow session) =>
        string.IsNullOrEmpty(session.ScheduleSeed)
            ? session.NextUnassignedKey is not null
            : session.NextChunkCursor < session.EpochChunkCount;

    private static void SetSessionState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sessionId,
        SearchSessionState state,
        DateTimeOffset now)
    {
        using var command = CreateCommand(connection, transaction,
            "UPDATE SearchSessions SET status = $status, updated_utc = $now WHERE id = $session_id;");
        command.Parameters.AddWithValue("$status", state.ToString());
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        command.Parameters.AddWithValue("$session_id", sessionId);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Session state update failed.");
    }

    private static void InsertAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sessionId,
        long? packageSequence,
        string eventType,
        string? workerId,
        string detail,
        DateTimeOffset now)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO AuditEvents(session_id, package_sequence, event_type, worker_id, detail, created_utc)
            VALUES($session_id, $sequence, $event_type, $worker_id, $detail, $now);
            """);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$sequence", packageSequence is null ? DBNull.Value : packageSequence.Value);
        command.Parameters.AddWithValue("$event_type", eventType);
        command.Parameters.AddWithValue("$worker_id", workerId is null ? DBNull.Value : workerId);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void ValidatePersistedIdentity(ApprovedPuzzle puzzle, string fingerprint, string rangeStart, string rangeEnd)
    {
        if (!string.Equals(fingerprint, puzzle.ManifestFingerprint, StringComparison.Ordinal) ||
            !string.Equals(rangeStart, puzzle.KeyRangeStart.ToString(), StringComparison.Ordinal) ||
            !string.Equals(rangeEnd, puzzle.KeyRangeEnd.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Checkpoint manifest fingerprint or approved range does not match this build.");
        }
    }

    private static void ValidateWorkerAndLease(string workerId, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 128) throw new ArgumentOutOfRangeException(nameof(workerId));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    private static void ValidateProgress(WorkPackageLease lease, UInt256 nextKey, ulong checkedKeys, double speed)
    {
        if (nextKey < lease.Package.Range.Start || nextKey > lease.Package.Range.End)
        {
            throw new ArgumentOutOfRangeException(nameof(nextKey), "Checkpoint must identify the next untested key inside the leased range.");
        }

        if (nextKey < lease.NextKey) throw new InvalidOperationException("Checkpoint progress cannot move backwards.");
        if (checkedKeys < lease.CheckedKeys) throw new InvalidOperationException("Checked key counter cannot move backwards.");
        var testedThroughNext = nextKey.ToBigInteger() - lease.Package.Range.Start.ToBigInteger();
        if (testedThroughNext.Sign < 0 || new BigInteger(checkedKeys) > testedThroughNext)
        {
            throw new InvalidOperationException("Checkpoint counter exceeds the number of keys before next_key.");
        }
        ValidateCounterAndSpeed(checkedKeys, speed);
    }

    private static void ValidatePackageCheckedCount(WorkPackageLease lease, ulong checkedKeys)
    {
        var packageSize = lease.Package.Range.End.ToBigInteger() - lease.Package.Range.Start.ToBigInteger() + BigInteger.One;
        if (new BigInteger(checkedKeys) > packageSize)
        {
            throw new InvalidOperationException("Completed counter exceeds the leased package size.");
        }
    }

    private static void ValidateCounterAndSpeed(ulong checkedKeys, double speed)
    {
        if (checkedKeys > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(checkedKeys));
        if (!double.IsFinite(speed) || speed < 0) throw new ArgumentOutOfRangeException(nameof(speed));
    }

    private static SearchSessionState ParseSessionState(string value) => value switch
    {
        "Running" => SearchSessionState.Running,
        "Paused" => SearchSessionState.Paused,
        "Completed" => SearchSessionState.Completed,
        "Found" => SearchSessionState.Found,
        "Stopped" => SearchSessionState.Stopped,
        _ => throw new InvalidDataException($"Unknown checkpoint session state '{value}'.")
    };

    private static BigInteger ParseBigInteger(string value, string field)
    {
        if (!BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed.Sign < 0)
        {
            throw new InvalidDataException($"Checkpoint field '{field}' is not a non-negative integer.");
        }

        return parsed;
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();

    private sealed record SessionRow(
        long KeysPerPackage,
        long NextSequence,
        UInt256? NextUnassignedKey,
        long CurrentEpoch,
        string ScheduleSeed,
        BigInteger NextChunkCursor,
        BigInteger EpochChunkCount,
        SearchSessionState State);

    private sealed record PackageRow(WorkPackage Package, UInt256 NextKey, ulong CheckedKeys)
    {
        public long Sequence => Package.Sequence;
    }
}
