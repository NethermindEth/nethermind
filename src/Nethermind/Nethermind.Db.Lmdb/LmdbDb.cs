// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using LightningDB;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// <see cref="IDb"/> backed by one on-disk LMDB environment (copy-on-write B+tree over a memory-mapped file -
/// see PLAN-LMDB.md). Point reads open their own short-lived read-only transaction, mirroring the BTDB
/// experiment's <c>BtdbDb.Get</c> - LMDB read-only transaction acquire is cheap (no locks, just a reader-table
/// slot), so this is not the per-call cost RocksDB's transaction-free <c>Get</c> avoids paying, but it keeps the
/// implementation simple and gives every read a consistent point-in-time view. Writes go through a group-commit
/// coordinator instead of one transaction per caller - see <see cref="SubmitWrite"/>.
/// </summary>
public sealed class LmdbDb : IDb, ISortedKeyValueStore, IKeyValueStoreWithSnapshot
{
    private readonly string _path;
    private readonly LightningEnvironment _env;

    /// <summary>
    /// One entry when this column is a single flat keyspace (the default/historical layout - one unnamed LMDB
    /// database), or one per account-range sync partition when <see cref="ILmdbConfig.EnableAccountPartitioning"/>
    /// is on (named databases "p0".."p{N-1}") - see that config property's remarks for why only Account ever uses
    /// more than one, and <see cref="GetPartitionIndex"/> for the key-to-partition mapping, which must mirror
    /// <c>ProgressTracker.SetupAccountRangePartition</c> exactly. Entries are ordered ascending by key range (an
    /// invariant <see cref="GetPartitionIndex"/>'s math and <c>ProgressTracker</c>'s both independently produce),
    /// which every multi-partition scan below (GetAll/FirstKey/LastKey/EstimatedCount) relies on to stay correctly
    /// sorted without an explicit merge step.
    /// </summary>
    private readonly LightningDatabase[] _databases;

    private bool _disposed;

    /// <summary>
    /// Guards two things that must never interleave: (1) the moment a write's <c>Commit()</c> becomes visible to
    /// new read-only transactions against <see cref="CreateSnapshot"/> opening its reader pool (see that
    /// method's remarks - mirrors BTDB's <c>_writeCommitGate</c>), and (2) growing <see cref="LightningEnvironment.MapSize"/>
    /// on a <see cref="MDBResultCode.MapFull"/> retry, which the verification spike (see PLAN-LMDB.md "Spike
    /// findings") confirmed only throws when a write transaction is active - by construction there is none while
    /// this lock is held outside of <see cref="WriterThreadLoop"/>'s own write-transaction section.
    /// </summary>
    private readonly Lock _writeCommitGate = new();

    // Diagnostics only (see GetDiagnostics/LmdbColumnsDbAdapter's periodic logger) - counts and total elapsed
    // ticks per operation kind, so a live run can report call volume and average latency for each without needing
    // an external profiler attached. Interlocked rather than a lock: these are updated on every single Get/
    // CreateSnapshot/write-wave, so any added synchronization here would itself skew exactly the latency this
    // exists to measure.
    private long _getCount;
    private long _getElapsedTicks;
    private long _createSnapshotCount;
    private long _createSnapshotElapsedTicks;
    private long _writeWaveCount;
    private long _writeWaveElapsedTicks;
    private long _writeWaveOpsCount;
    private long _mapGrowthCount;
    private long _totalPutCount;
    private long _appendedPutCount;

    // Phase breakdown of _writeWaveElapsedTicks (see CommitOperationsWithMapGrowthRetry) - added specifically to
    // answer "where does a slow physical commit's time actually go": real B+tree insert cost (apply), the
    // flush/meta-page step (commit), or just acquiring the write transaction (begin) - every diagnostic before
    // this one could only report the wave as one opaque number.
    private long _writeBeginElapsedTicks;
    private long _writeApplyElapsedTicks;
    private long _writeCommitElapsedTicks;
    private long _writeCommitLockWaitElapsedTicks;
    private long _writeCommitCallElapsedTicks;

    /// <summary>
    /// Per-operation (not per-wave) apply-phase cost, split by whether that specific partition-group's Put loop
    /// used <see cref="PutOptions.AppendData"/> or not - added specifically to get a true live measurement of
    /// AppendData's effect, after every isolated synthetic benchmark (see PLAN-LMDB.md "Known issues") showed a
    /// robust 7-34x advantage that live cross-run comparisons couldn't detect, because engagement is fully
    /// automatic (self-verifying, not independently toggleable) and every prior live comparison was confounded by
    /// comparing across different sync runs at different database sizes. A single wave can - and in Storage's
    /// case, given its ~20% engagement, routinely does - contain both appended and non-appended partition-groups
    /// side by side, so this needs no toggle or restart to get a genuine in-situ A/B signal.
    /// </summary>
    private long _appendedApplyElapsedTicks;
    private long _appendedApplyOpsCount;
    private long _nonAppendedApplyElapsedTicks;
    private long _nonAppendedApplyOpsCount;

    /// <summary>
    /// Total pooled read transactions currently alive across every not-yet-disposed <see cref="LmdbDbSnapshot"/>
    /// - i.e. live LMDB reader-table occupancy from this column's snapshots specifically (not counting the much
    /// shorter-lived per-call transactions <see cref="Get"/> etc. use). Logged alongside slow-event warnings -
    /// see PLAN-LMDB.md "Known issues": LMDB's write-commit cost is documented to scale with the number of
    /// concurrently active readers (it must find the oldest one to know which old pages are safe to reclaim for
    /// copy-on-write reuse), independent of the committing write's own size - a high count here during a slow
    /// write, with disk I/O, GC, and ThreadPool capacity all ruled out, would be direct evidence of exactly that.
    /// </summary>
    private long _aliveSnapshotPoolReaders;

    public string Name { get; }

    private readonly int _maxOperationsPerWave;
    private readonly ILogger _logger;

    /// <summary>Above this, a single write request or CreateSnapshot call is logged individually (at Warn, so
    /// it's visible without raising log verbosity) rather than only folded into the periodic diagnostics average
    /// - see PLAN-LMDB.md "Known issues" for why per-event outlier visibility matters here: a slow individual
    /// commit blocks every concurrent CreateSnapshot reader for its own duration, and averages can hide exactly
    /// the outlier that explains a specific stall.</summary>
    private const long SlowOperationThresholdMs = 100;

    public LmdbDb(string name, string path, long initialMapSizeBytes, int maxReaders, int maxOperationsPerWave, int partitionCount, ILogManager logManager, int pageSizeBytes = 0)
    {
        Name = name;
        _path = path;
        _maxOperationsPerWave = maxOperationsPerWave;
        _logger = logManager.GetClassLogger<LmdbDb>();
        Directory.CreateDirectory(path);

        partitionCount = Math.Max(1, partitionCount);

        // NoThreadLocalStorage: CreateSnapshot's reader pool hands pooled read transactions out via rent/return,
        // so a given transaction's using thread can legitimately change between rentals - the default
        // TLS-slot-per-thread reader binding is not designed for that pattern (see PLAN-LMDB.md spike finding 2).
        // MaxReaders defaults to only 126 in LMDB itself, which CreateSnapshot's pool of
        // Environment.ProcessorCount readers per snapshot burns through with just a handful of concurrently-alive
        // snapshots on a many-core box (observed in practice: MDB_READERS_FULL) - see BeginReadOnlyTransaction.
        // MaxDatabases only needs to cover named databases (the unnamed default database below doesn't count
        // against it), so it is left at its default of 0 in the single-partition (default) case.
        EnvironmentConfiguration envConfiguration = new() { MapSize = initialMapSizeBytes, MaxReaders = maxReaders };
        if (partitionCount > 1) envConfiguration.MaxDatabases = partitionCount;
        // 0 (the default) leaves LMDB's own default (the OS page size) in place - see ILmdbConfig.PageSizeBytes's
        // remarks for why a different value might help, and PLAN-LMDB.md "mdb_stat live diagnostics" for the live
        // data this is meant to be tuned from. Only takes effect for a brand new environment - LMDB stores the
        // page size persistently in the environment at creation time, so this has no effect when reopening an
        // existing one (not yet verified whether LMDB silently ignores a mismatched value here or errors - treat
        // changing this on an existing directory as unverified until checked).
        if (pageSizeBytes > 0) envConfiguration.PageSize = pageSizeBytes;
        _env = new LightningEnvironment(path, envConfiguration);

        // Everything past this point must dispose _env before throwing - an exception out of a constructor
        // leaves no object for a caller to call Dispose() on, and an undisposed LightningEnvironment crashes the
        // *entire process* from its finalizer (see BeginReadOnlyTransaction's remarks on this exact failure
        // mode; observed in practice when the guard below threw without this try/catch).
        try
        {
            _env.Open(EnvironmentOpenFlags.NoThreadLocalStorage);

            using LightningTransaction tx = _env.BeginTransaction();

            if (partitionCount > 1)
            {
                // Partitioned mode uses separate named databases that a non-partitioned sync never wrote to - if
                // the default database already has data, silently "losing" it (reads would just see nothing,
                // with no error) would be far worse than refusing to start. See
                // ILmdbConfig.EnableAccountPartitioning's remarks.
                using LightningDatabase defaultDatabase = tx.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
                long existingUnpartitionedEntries = tx.GetEntriesCount(defaultDatabase);
                if (existingUnpartitionedEntries > 0)
                {
                    throw new InvalidOperationException(
                        $"Lmdb '{name}' at '{path}' already has {existingUnpartitionedEntries:N0} entries in its " +
                        "default (non-partitioned) database, but partitioning is now enabled. Partitioned mode " +
                        "reads and writes separate named databases and cannot see this pre-existing data - wipe " +
                        "this directory to start a fresh partitioned sync, or disable partitioning to keep " +
                        "reading the existing data.");
                }
            }

            _databases = new LightningDatabase[partitionCount];
            for (int i = 0; i < partitionCount; i++)
            {
                DatabaseConfiguration dbConfiguration = new() { Flags = DatabaseOpenFlags.Create };
                _databases[i] = partitionCount == 1
                    ? tx.OpenDatabase(configuration: dbConfiguration, closeOnDispose: false)
                    : tx.OpenDatabase(name: $"p{i}", configuration: dbConfiguration, closeOnDispose: false);
            }

            tx.Commit();
        }
        catch
        {
            _env.Dispose();
            throw;
        }

        // Started only once construction has fully succeeded - WriterThreadLoop touches _env/_databases, both of
        // which must be in their final state before any write can be drained.
        _writerThread = new Thread(WriterThreadLoop) { IsBackground = true, Name = $"Lmdb.Writer.{name}" };
        _writerThread.Start();
    }

    /// <summary>
    /// Maps a key to which entry of <see cref="_databases"/> it belongs to - mirrors
    /// <c>ProgressTracker.SetupAccountRangePartition</c>'s own boundary math exactly (equal-sized ranges over the
    /// key's first 4 bytes, big-endian, with any remainder from a non-power-of-two partition count folded into
    /// the last partition), since that is the actual grouping snap sync's account-range phase delivers data in.
    /// Correctness does not depend on that mirroring for keys this plugin merely stores and retrieves by exact
    /// key - this is a pure, deterministic function of the key's own bytes, so the same key always lands in the
    /// same partition for every Get/Put/Delete regardless of whether the boundary happens to line up with
    /// anything semantically meaningful. It only matters for whether <see cref="CanUseAppendData"/> actually gets
    /// to engage more often, which is the entire point of partitioning at all. Keys shorter than 4 bytes (not
    /// expected in practice - see <see cref="ILmdbConfig.EnableAccountPartitioning"/>) deterministically map to
    /// partition 0 rather than throwing, for the same reason: consistency, not semantic correctness, is what
    /// every other method here actually depends on.
    /// </summary>
    private static int GetPartitionIndex(ReadOnlySpan<byte> key, int partitionCount)
    {
        if (partitionCount <= 1) return 0;
        if (key.Length < sizeof(uint)) return 0;

        uint partitionSize = (uint)(((ulong)uint.MaxValue + 1) / (ulong)partitionCount);
        uint firstWord = BinaryPrimitives.ReadUInt32BigEndian(key);
        int index = (int)(firstWord / partitionSize);

        // Only the last partition can be larger than partitionSize (it absorbs uint.MaxValue+1's remainder when
        // partitionCount doesn't divide it evenly) - without this clamp, a key in that remainder range would
        // compute an out-of-bounds index instead of correctly falling into the last partition.
        return Math.Min(index, partitionCount - 1);
    }

    private LightningDatabase GetDatabase(ReadOnlySpan<byte> key) => _databases[GetPartitionIndex(key, _databases.Length)];

    /// <summary>
    /// Opens a read-only transaction. Do not wrap this in a catch-and-retry for <see cref="MDBResultCode.ReadersFull"/>:
    /// the verification spike (see PLAN-LMDB.md "Known issues") confirmed that in LightningDB 0.23.0, a *failed*
    /// <see cref="LightningEnvironment.BeginTransaction(TransactionBeginFlags)"/> call leaks an internal
    /// transaction handle that the library still tracks against this environment - one that this method never
    /// gets a reference to and so can never dispose. That handle later crashes the *entire process* with an
    /// unrecoverable "must be disposed before closing the environment" exception thrown from a finalizer, on some
    /// unrelated later GC, **even if a subsequent retry on the same call succeeds**. Catching and retrying only
    /// hides the immediate symptom while leaving the process just as compromised - worse, actually, since the
    /// eventual crash then has no visible connection to its real cause. The only real defense is never letting
    /// the reader table fill up in the first place: size <see cref="ILmdbConfig.MaxReaders"/> comfortably above
    /// the realistic concurrent load (<see cref="CreateSnapshot"/>'s pool size times how many snapshots can be
    /// alive at once). If this ever throws in practice, treat it as a signal to raise that config value, not as
    /// something to handle at the call site.
    /// </summary>
    private LightningTransaction BeginReadOnlyTransaction() => _env.BeginTransaction(TransactionBeginFlags.ReadOnly);

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        long start = Stopwatch.GetTimestamp();
        using LightningTransaction tx = BeginReadOnlyTransaction();
        (MDBResultCode rc, _, MDBValue value) = tx.Get(GetDatabase(key), key);
        byte[]? result = rc == MDBResultCode.Success ? value.CopyToNewArray() : null;
        Interlocked.Increment(ref _getCount);
        Interlocked.Add(ref _getElapsedTicks, Stopwatch.GetTimestamp() - start);
        return result;
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        SubmitWrite([(key.ToArray(), value)], wantsDurability: !flags.HasFlag(WriteFlags.DisableWAL));

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            KeyValuePair<byte[], byte[]?>[] result = new KeyValuePair<byte[], byte[]?>[keys.Length];
            using LightningTransaction tx = BeginReadOnlyTransaction();
            for (int i = 0; i < keys.Length; i++)
            {
                byte[] key = keys[i];
                (MDBResultCode rc, _, MDBValue value) = tx.Get(GetDatabase(key), key);
                result[i] = new KeyValuePair<byte[], byte[]?>(key, rc == MDBResultCode.Success ? value.CopyToNewArray() : null);
            }

            return result;
        }
    }

    public IWriteBatch StartWriteBatch() => new LmdbWriteBatch(this);

    /// <summary>
    /// Scans every partition's database in ascending order (one cursor at a time, all under the same read
    /// transaction for a single consistent point-in-time view) - correct without an explicit merge step because
    /// <see cref="_databases"/> is itself already ordered ascending by key range (see that field's remarks).
    /// </summary>
    public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
    {
        // LMDB's own index is a B+tree, so it is always ordered - the flag has nothing extra to opt into.
        using LightningTransaction tx = BeginReadOnlyTransaction();
        foreach (LightningDatabase database in _databases)
        {
            using LightningCursor cursor = tx.CreateCursor(database);
            (MDBResultCode rc, MDBValue key, MDBValue value) = cursor.First();
            while (rc == MDBResultCode.Success)
            {
                yield return new KeyValuePair<byte[], byte[]>(key.CopyToNewArray(), value.CopyToNewArray());
                (rc, key, value) = cursor.Next();
            }
        }
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        using LightningTransaction tx = BeginReadOnlyTransaction();
        foreach (LightningDatabase database in _databases)
        {
            using LightningCursor cursor = tx.CreateCursor(database);
            (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.First();
            while (rc == MDBResultCode.Success)
            {
                yield return key.CopyToNewArray();
                (rc, key, _) = cursor.Next();
            }
        }
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        using LightningTransaction tx = BeginReadOnlyTransaction();
        foreach (LightningDatabase database in _databases)
        {
            using LightningCursor cursor = tx.CreateCursor(database);
            (MDBResultCode rc, MDBValue _, MDBValue value) = cursor.First();
            while (rc == MDBResultCode.Success)
            {
                yield return value.CopyToNewArray();
                (rc, _, value) = cursor.Next();
            }
        }
    }

    public byte[]? FirstKey
    {
        get
        {
            using LightningTransaction tx = BeginReadOnlyTransaction();
            foreach (LightningDatabase database in _databases)
            {
                using LightningCursor cursor = tx.CreateCursor(database);
                (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.First();
                if (rc == MDBResultCode.Success) return key.CopyToNewArray();
            }

            return null;
        }
    }

    public byte[]? LastKey
    {
        get
        {
            using LightningTransaction tx = BeginReadOnlyTransaction();
            for (int i = _databases.Length - 1; i >= 0; i--)
            {
                using LightningCursor cursor = tx.CreateCursor(_databases[i]);
                (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.Last();
                if (rc == MDBResultCode.Success) return key.CopyToNewArray();
            }

            return null;
        }
    }

    /// <summary>
    /// Splits <c>[firstKeyInclusive, lastKeyExclusive)</c> across every partition it actually intersects (just
    /// one in the common, non-partitioned case) and merges them into a single <see cref="ISortedView"/> - see
    /// <see cref="LmdbMultiPartitionSortedView"/>. Each intersecting partition gets its own cursor, but all of
    /// them share one transaction so the whole merged view is one consistent point-in-time snapshot, exactly like
    /// the single-partition case before this.
    /// </summary>
    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
    {
        byte[] first = firstKeyInclusive.ToArray();
        byte[] last = lastKeyExclusive.ToArray();
        (int PartitionIndex, byte[] EffectiveFirst, byte[] EffectiveLast)[] parts = GetIntersectingPartitions(_databases.Length, first, last);

        LightningTransaction tx = BeginReadOnlyTransaction();
        (int PartitionIndex, LmdbSortedView View)[] views = new (int, LmdbSortedView)[parts.Length];
        int built = 0;
        try
        {
            for (; built < parts.Length; built++)
            {
                LightningCursor cursor = tx.CreateCursor(_databases[parts[built].PartitionIndex]);
                views[built] = (parts[built].PartitionIndex, new LmdbSortedView(tx, cursor, parts[built].EffectiveFirst, parts[built].EffectiveLast, ownsTransaction: false));
            }

            return new LmdbMultiPartitionSortedView(views, _databases.Length, tx, ownsTransaction: true);
        }
        catch
        {
            for (int i = 0; i < built; i++) views[i].View.Dispose();
            tx.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The exclusive-upper-bound key of partition <paramref name="partitionIndex"/> among <paramref name="partitionCount"/>
    /// total partitions - i.e. the first key that belongs to partition <paramref name="partitionIndex"/> (also
    /// partition <paramref name="partitionIndex"/> - 1's exclusive upper bound). Only the first 4 bytes of a key
    /// ever decide its partition (see <see cref="GetPartitionIndex"/>), so a 4-byte boundary key compares
    /// correctly against keys of any real length: <see cref="Bytes.BytesComparer"/>'s lexicographic ordering
    /// treats a strict prefix as smaller than anything longer starting with it, which is exactly the inclusive
    /// lower / exclusive upper bound semantics needed here.
    /// </summary>
    private static byte[] PartitionBoundaryKey(int partitionIndex, int partitionCount)
    {
        uint partitionSize = (uint)(((ulong)uint.MaxValue + 1) / (ulong)partitionCount);
        byte[] bound = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bound, (uint)partitionIndex * partitionSize);
        return bound;
    }

    /// <summary>
    /// Which partitions overlap <c>[firstKeyInclusive, lastKeyExclusive)</c>, each clipped to the intersection of
    /// its own key range with that query range - see <see cref="GetViewBetween"/>. Reduces to exactly one entry,
    /// <c>(0, firstKeyInclusive, lastKeyExclusive)</c> unclipped, when <paramref name="partitionCount"/> is 1 (no
    /// special-casing needed: partition 0's own range is the whole keyspace, so clipping against it is a no-op).
    /// </summary>
    private static (int PartitionIndex, byte[] EffectiveFirst, byte[] EffectiveLast)[] GetIntersectingPartitions(int partitionCount, byte[] firstKeyInclusive, byte[] lastKeyExclusive)
    {
        List<(int, byte[], byte[])> result = new(partitionCount);
        for (int i = 0; i < partitionCount; i++)
        {
            byte[] partitionLower = i == 0 ? [] : PartitionBoundaryKey(i, partitionCount);
            byte[]? partitionUpperExclusive = i == partitionCount - 1 ? null : PartitionBoundaryKey(i + 1, partitionCount);

            bool startsAtOrPastQueryEnd = Bytes.BytesComparer.Compare(partitionLower, lastKeyExclusive) >= 0;
            bool endsAtOrBeforeQueryStart = partitionUpperExclusive is not null && Bytes.BytesComparer.Compare(partitionUpperExclusive, firstKeyInclusive) <= 0;
            if (startsAtOrPastQueryEnd || endsAtOrBeforeQueryStart) continue;

            byte[] effectiveFirst = Bytes.BytesComparer.Compare(firstKeyInclusive, partitionLower) > 0 ? firstKeyInclusive : partitionLower;
            byte[] effectiveLast = partitionUpperExclusive is null
                ? lastKeyExclusive
                : Bytes.BytesComparer.Compare(lastKeyExclusive, partitionUpperExclusive) < 0 ? lastKeyExclusive : partitionUpperExclusive;

            result.Add((i, effectiveFirst, effectiveLast));
        }

        return result.ToArray();
    }

    /// <summary>
    /// Number of independent read-only transactions <see cref="CreateSnapshot"/> opens per snapshot, one per
    /// concurrent reader the pool can serve without any of them waiting - see <see cref="LmdbDbSnapshot"/>'s
    /// remarks for why a pool replaces one shared transaction (mirrors BTDB bug #7's fix).
    /// </summary>
    private static readonly int SnapshotReaderPoolSize = Environment.ProcessorCount;

    /// <summary>
    /// FlatDb's RocksDB-backed persistence layer (<c>RocksDbPersistence.CreateReader</c>/<c>CreateWriteBatch</c>)
    /// takes a column-DB-wide snapshot on every read/write-batch open, including at node startup via
    /// <c>FlatStateBoundary</c> - this is not an edge case, so it must return a real point-in-time view rather
    /// than throw. An LMDB read-only transaction is, by construction, already an MVCC snapshot of the
    /// last-committed root, isolated from subsequent commits - exactly what is needed here.
    /// </summary>
    /// <remarks>
    /// Opens a whole pool of <see cref="SnapshotReaderPoolSize"/> read-only transactions rather than one - see
    /// <see cref="LmdbDbSnapshot"/>'s remarks. All of them must observe the identical last-committed version, so
    /// they are opened back-to-back under <see cref="_writeCommitGate"/>, the same lock
    /// <see cref="CommitOperationsWithMapGrowthRetry"/> holds around its own <c>Commit()</c> call: this guarantees
    /// no commit can become visible partway through
    /// the loop below, which would otherwise split the pool across two versions.
    /// </remarks>
    public IKeyValueStoreSnapshot CreateSnapshot()
    {
        long start = Stopwatch.GetTimestamp();
        TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();
        LightningTransaction[] pool = new LightningTransaction[SnapshotReaderPoolSize];
        int opened = 0;
        try
        {
            lock (_writeCommitGate)
            {
                for (; opened < pool.Length; opened++) pool[opened] = BeginReadOnlyTransaction();
            }
        }
        catch
        {
            // A failure partway through must not strand the *successfully* opened transactions earlier in this
            // same loop - each already consumes a real reader-table slot that would otherwise never be released.
            // This does not fully mitigate the failure itself - see BeginReadOnlyTransaction's remarks on why a
            // failure here is far more serious than a normal recoverable error.
            for (int i = 0; i < opened; i++) pool[i].Dispose();
            throw;
        }

        long aliveReaders = Interlocked.Add(ref _aliveSnapshotPoolReaders, pool.Length);

        long elapsedTicks = Stopwatch.GetTimestamp() - start;
        Interlocked.Increment(ref _createSnapshotCount);
        Interlocked.Add(ref _createSnapshotElapsedTicks, elapsedTicks);

        double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= SlowOperationThresholdMs && _logger.IsWarn)
        {
            double gcPauseDuringMs = (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds;
            _logger.Warn($"Lmdb '{Name}' CreateSnapshot took {elapsedMs:F1}ms (pool size {pool.Length}, alive pooled readers: {aliveReaders}, GC pause during: {gcPauseDuringMs:F1}ms, {FormatThreadPoolState()}) - every concurrent reader on this column was blocked for this duration.");
        }

        return new LmdbDbSnapshot(pool, _databases, this);
    }

    /// <summary>
    /// LMDB has no separate WAL/data-file split the way RocksDB does (one mmap'd file, no compaction), so
    /// <paramref name="onlyWal"/> has nothing distinct to opt into - either way this forces a synchronous flush
    /// of whatever has been committed so far (<c>mdb_env_sync(force: true)</c> via <see cref="LightningEnvironment.Flush"/>),
    /// which is the closest LMDB equivalent to RocksDB's <c>Flush(onlyWal: true)</c>.
    /// </summary>
    public void Flush(bool onlyWal = false) => _env.Flush(force: true);

    public long EstimatedCount
    {
        get
        {
            using LightningTransaction tx = BeginReadOnlyTransaction();
            long total = 0;
            foreach (LightningDatabase database in _databases) total += tx.GetEntriesCount(database);
            return total;
        }
    }


    /// <summary><c>mdb_env_stat</c> (<see cref="LightningEnvironment.EnvironmentStats"/>) - see PLAN-LMDB.md
    /// "mdb_env_stat live diagnostics". Added specifically to observe real B+tree page-level structure during a
    /// live sync (depth, page counts, entries-per-leaf-page fill efficiency), to inform whether a non-default
    /// <see cref="ILmdbConfig.PageSizeBytes"/> would help.</summary>
    /// <remarks>
    /// <c>mdb_env_stat</c> reports on the environment's root database, not a specific named one - unlike
    /// <c>mdb_stat(txn, dbi, ...)</c>, it takes only the environment handle, no transaction or dbi. For a single
    /// unnamed database (the default, and Storage's current shape), the root database <em>is</em> the actual
    /// data, so this reports real figures directly. For a partitioned (named-database) environment - Account,
    /// when <see cref="ILmdbConfig.EnableAccountPartitioning"/> is on - the root database instead holds the
    /// catalog mapping partition names to their own separate sub-databases, so this reports the tiny catalog's
    /// own stats, not the real per-partition data; <see cref="GetPerPartitionStats"/> covers that case via
    /// <c>mdb_stat</c> per named database instead.
    /// </remarks>
    internal global::LightningDB.Stats GetEnvironmentStats() => _env.EnvironmentStats;

    /// <summary>Per-partition <c>mdb_stat</c> (<see cref="LightningTransaction.GetStats"/>), aggregated across
    /// every partition's own named database - the counterpart to <see cref="GetEnvironmentStats"/> for a
    /// partitioned (named-database) environment, where <c>mdb_env_stat</c> only sees the catalog, not the real
    /// per-partition data. Identical to <see cref="GetEnvironmentStats"/> in the single, unnamed-database case
    /// (<c>_databases.Length == 1</c>), since the one database's own <c>mdb_stat</c> and the environment's root
    /// <c>mdb_env_stat</c> are then the same database.</summary>
    internal global::LightningDB.Stats GetPerPartitionStats()
    {
        using LightningTransaction tx = BeginReadOnlyTransaction();
        long pageSize = 0, maxDepth = 0, branchPages = 0, leafPages = 0, overflowPages = 0, entries = 0;
        foreach (LightningDatabase database in _databases)
        {
            global::LightningDB.Stats s = tx.GetStats(database);
            pageSize = s.PageSize;
            maxDepth = Math.Max(maxDepth, s.BTreeDepth);
            branchPages += s.BranchPages;
            leafPages += s.LeafPages;
            overflowPages += s.OverflowPages;
            entries += s.Entries;
        }

        return new global::LightningDB.Stats { PageSize = pageSize, BTreeDepth = maxDepth, BranchPages = branchPages, LeafPages = leafPages, OverflowPages = overflowPages, Entries = entries };
    }

    /// <summary>Call volume and average latency, for the interval since the last call to
    /// <see cref="TakeAndResetDiagnostics"/>, for the three operation kinds most likely to matter for the open
    /// "why does this feel slow" question (see PLAN-LMDB.md "Validation methodology") - point reads, snapshot
    /// creation (the potentially-expensive one: opens <see cref="SnapshotReaderPoolSize"/> transactions per
    /// call), and physical write-wave commits. Consumed by <see cref="LmdbColumnsDbAdapter"/>'s periodic logger;
    /// not itself logged by <see cref="LmdbDb"/>.</summary>
    internal readonly record struct DiagnosticsSnapshot(
        long GetCount, double AvgGetMicros,
        long CreateSnapshotCount, double AvgCreateSnapshotMicros,
        long WriteWaveCount, double AvgWriteWaveMicros, double AvgWriteWaveOps,
        long MapGrowthCount,
        long AliveSnapshotPoolReaders,
        long TotalPutCount, long AppendedPutCount,
        double AvgWriteBeginMicros, double AvgWriteApplyMicros, double AvgWriteCommitMicros,
        double AvgWriteCommitLockWaitMicros, double AvgWriteCommitCallMicros,
        long AppendedApplyOpsCount, double AvgAppendedApplyMicrosPerOp,
        long NonAppendedApplyOpsCount, double AvgNonAppendedApplyMicrosPerOp);

    /// <summary>Atomically reads and zeroes every counter, so each call reports only the interval since the
    /// previous one rather than a lifetime-since-process-start average that would dilute an early spike into
    /// invisibility over a long-running node. Each operation's count and elapsed-ticks pair is reset via two
    /// separate <see cref="Interlocked.Exchange(ref long, long)"/> calls, not one atomic pair - a call landing in
    /// the narrow window between them can be undercounted or miscounted into the wrong interval. Acceptable for a
    /// rough diagnostic signal; not a billing-grade counter.</summary>
    internal DiagnosticsSnapshot TakeAndResetDiagnostics()
    {
        long getCount = Interlocked.Exchange(ref _getCount, 0);
        long createSnapshotCount = Interlocked.Exchange(ref _createSnapshotCount, 0);
        long writeWaveCount = Interlocked.Exchange(ref _writeWaveCount, 0);
        long writeWaveOpsCount = Interlocked.Exchange(ref _writeWaveOpsCount, 0);
        long appendedApplyOpsCount = Interlocked.Exchange(ref _appendedApplyOpsCount, 0);
        long nonAppendedApplyOpsCount = Interlocked.Exchange(ref _nonAppendedApplyOpsCount, 0);
        return new DiagnosticsSnapshot(
            getCount, AvgMicros(Interlocked.Exchange(ref _getElapsedTicks, 0), getCount),
            createSnapshotCount, AvgMicros(Interlocked.Exchange(ref _createSnapshotElapsedTicks, 0), createSnapshotCount),
            writeWaveCount, AvgMicros(Interlocked.Exchange(ref _writeWaveElapsedTicks, 0), writeWaveCount),
            writeWaveCount == 0 ? 0 : (double)writeWaveOpsCount / writeWaveCount,
            Interlocked.Exchange(ref _mapGrowthCount, 0),
            Interlocked.Read(ref _aliveSnapshotPoolReaders),
            Interlocked.Exchange(ref _totalPutCount, 0),
            Interlocked.Exchange(ref _appendedPutCount, 0),
            AvgMicros(Interlocked.Exchange(ref _writeBeginElapsedTicks, 0), writeWaveCount),
            AvgMicros(Interlocked.Exchange(ref _writeApplyElapsedTicks, 0), writeWaveCount),
            AvgMicros(Interlocked.Exchange(ref _writeCommitElapsedTicks, 0), writeWaveCount),
            AvgMicros(Interlocked.Exchange(ref _writeCommitLockWaitElapsedTicks, 0), writeWaveCount),
            AvgMicros(Interlocked.Exchange(ref _writeCommitCallElapsedTicks, 0), writeWaveCount),
            appendedApplyOpsCount, AvgMicros(Interlocked.Exchange(ref _appendedApplyElapsedTicks, 0), appendedApplyOpsCount),
            nonAppendedApplyOpsCount, AvgMicros(Interlocked.Exchange(ref _nonAppendedApplyElapsedTicks, 0), nonAppendedApplyOpsCount));

        static double AvgMicros(long totalTicks, long count) =>
            count == 0 ? 0 : totalTicks * 1_000_000.0 / Stopwatch.Frequency / count;
    }

    public IDbMeta.DbMetric GatherMetric()
    {
        long size = 0;
        if (Directory.Exists(_path))
        {
            foreach (string file in Directory.EnumerateFiles(_path))
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // A concurrent writer/resize could rename or replace a file between the enumeration and the
                    // stat call; a best-effort metric skips it rather than failing the whole gather.
                }
            }
        }

        return new IDbMeta.DbMetric { Size = size };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signals WriterThreadLoop to exit after it finishes draining whatever is currently queued (not
        // mid-drain) - see that method's remarks. Joining before disposing _env/_databases guarantees the writer
        // thread is never still touching either of them when they go away.
        _stopWriterThread = true;
        _pendingWritesAvailable.Set();
        _writerThread.Join();

        foreach (LightningDatabase database in _databases) database.Dispose();
        _env.Dispose();
    }

    private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();
    private readonly ManualResetEventSlim _pendingWritesAvailable = new(initialState: false);
    private readonly Thread _writerThread;
    private volatile bool _stopWriterThread;

    /// <summary>Test-only visibility into the dedicated writer thread's lifecycle (started once in the
    /// constructor, joined in <see cref="Dispose"/>) - see <see cref="WriterThreadLoop"/>.</summary>
    internal bool IsWriterThreadAlive => _writerThread.IsAlive;

    /// <summary>Test-only visibility into the dedicated writer thread's name, confirming one actually exists per
    /// <see cref="LmdbDb"/> instance (named after <see cref="Name"/> for diagnosability) rather than work running
    /// on an arbitrary caller's thread.</summary>
    internal string? WriterThreadName => _writerThread.Name;

    /// <summary>
    /// Submits <paramref name="operations"/> (each a key with its new value, or <see langword="null"/> to
    /// delete) as one logical write and blocks until they are committed or the attempt fails.
    /// </summary>
    /// <remarks>
    /// LMDB allows only one write transaction per environment at a time (the verification spike confirmed
    /// <see cref="LightningEnvironment.BeginTransaction(TransactionBeginFlags)"/> blocks the caller until the
    /// previous writer commits, rather than throwing a busy error - see PLAN-LMDB.md "Spike findings" #1).
    /// Nethermind's snap-sync dispatcher writes Account/Storage ranges from many concurrent tasks; giving each
    /// its own physical transaction against a single-writer store reproduces BTDB bug #4 (most wall-clock time
    /// spent simply queued for a turn). Coalescing many callers' operations into one physical transaction per
    /// drained wave (see <see cref="WriterThreadLoop"/>) removes the per-caller acquire/park/wake overhead without
    /// raising the single-writer mutation ceiling, which was already fully serialized either way.
    /// </remarks>
    private void SubmitWrite(List<(byte[] Key, byte[]? Value)> operations, bool wantsDurability)
    {
        long start = Stopwatch.GetTimestamp();
        TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();
        long pageFaultsBefore = WindowsProcessDiagnostics.CachedPageFaultCount;
        PendingWrite write = new(operations, wantsDurability);
        _pendingWrites.Enqueue(write);
        _pendingWritesAvailable.Set();
        write.Done.Wait();
        write.Done.Dispose();

        // Per-request absolute timing for correlating a specific slow account-range/storage-range commit
        // against an external symptom (e.g. a consensus client's own logged timestamp for a newPayload
        // timeout) - see PLAN-LMDB.md "Known issues". Debug gives every request (opt-in verbosity, since this
        // fires once per snap-sync write batch - potentially hundreds/sec); Warn gives only the outliers, so a
        // real stall is visible without needing to raise log verbosity first. GC pause is logged alongside
        // specifically because a handful-of-operations write taking seconds (observed in practice: 6 ops,
        // ~6.9s) cannot be real B+tree work - the two main candidates are a GC pause freezing the whole process
        // (this write's own work is trivial either way) or time spent queued behind something else entirely.
        double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        double gcPauseDuringMs = (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds;
        if (_logger.IsDebug)
        {
            _logger.Debug($"Lmdb '{Name}' write request committed: {operations.Count} ops, durable={wantsDurability}, took {elapsedMs:F1}ms (GC pause during: {gcPauseDuringMs:F1}ms).");
        }
        else if (elapsedMs >= SlowOperationThresholdMs && _logger.IsWarn)
        {
            long pageFaultsDuring = WindowsProcessDiagnostics.ReadNow() - pageFaultsBefore;
            _logger.Warn($"Lmdb '{Name}' write request took {elapsedMs:F1}ms ({operations.Count} ops, durable={wantsDurability}, alive pooled readers: {Interlocked.Read(ref _aliveSnapshotPoolReaders)}, GC pause during: {gcPauseDuringMs:F1}ms, page faults during: {pageFaultsDuring:N0} (Windows only, 0 elsewhere), {FormatThreadPoolState()}) - unusually slow.");
        }

        write.Error?.Throw();
    }

    /// <summary>
    /// Snapshot of process-wide .NET ThreadPool state, logged alongside slow-event warnings - see PLAN-LMDB.md
    /// "Known issues". <see cref="SubmitWrite"/> blocks synchronously (<c>ManualResetEventSlim.Wait()</c>) for
    /// every caller, waiting on the single dedicated writer thread (see <see cref="WriterThreadLoop"/>) to commit
    /// its wave; if many concurrent callers do this at once
    /// (e.g. snap sync's dispatcher fanning out many write-submitting tasks), most of them occupy a real
    /// ThreadPool thread while doing nothing (no CPU, no I/O) - and the ThreadPool only grows by roughly one
    /// thread per 500ms-1s under sustained demand by design, which can turn a burst of concurrent writers into a
    /// multi-second stall for everyone regardless of how little work any individual one has to do. A low
    /// <c>avail</c> relative to <c>max</c>, together with a nonzero <c>queued</c>, at the moment of a slow event
    /// is direct evidence of exactly this - a real storage-engine or GC cost would not show ThreadPool
    /// exhaustion at all.
    /// </summary>
    private static string FormatThreadPoolState()
    {
        ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        return $"ThreadPool: {ThreadPool.ThreadCount} threads, {availWorker}/{maxWorker} worker avail, {availIo}/{maxIo} IO avail, {ThreadPool.PendingWorkItemCount} queued";
    }

    /// <summary>
    /// Body of the single dedicated writer thread, started once in the constructor and run for this
    /// <see cref="LmdbDb"/>'s whole lifetime - see PLAN-LMDB.md "Dedicated writer thread redesign". Replaces an
    /// earlier design where whichever caller's own thread won a compare-and-swap became the "leader" for one
    /// drain pass: that conflated "who happens to be submitting a write right now" with "who batches and
    /// commits," so the actual commit work ran on an arbitrary caller's thread (with that caller's own CPU
    /// scheduling, not a purpose-built one), and - measured live as the more significant problem - every
    /// transition from an empty queue back to non-empty required a fresh compare-and-swap race among whichever
    /// callers happened to be submitting at that instant, which could itself win before several near-simultaneous
    /// other callers had finished enqueuing, producing smaller waves than the actual concurrent write pressure
    /// would otherwise support.
    /// </summary>
    /// <remarks>
    /// A single persistent thread removes that per-wave leader-election overhead entirely and self-regulates
    /// batch size for free via the classic "group commit" effect: while this thread is busy committing one wave,
    /// every write submitted during that commit simply accumulates in <see cref="_pendingWrites"/> for the next
    /// drain pass to pick up - no artificial coalescing delay needed, and no per-wave race whose timing could
    /// strand concurrent callers across multiple separate small waves. <see cref="_pendingWritesAvailable"/> is
    /// reset <em>before</em> draining, not after waiting, which closes the same race the old leader-based design's
    /// own remarks described: a write enqueued (and <see cref="ManualResetEventSlim.Set"/>) at any point between
    /// this reset and the next <see cref="ManualResetEventSlim.Wait()"/> call - including mid-drain - guarantees
    /// that next <c>Wait()</c> call returns immediately rather than blocking, so no enqueued write can ever be
    /// stranded until some unrelated future write happens to wake this thread up.
    /// </remarks>
    private void WriterThreadLoop()
    {
        while (true)
        {
            _pendingWritesAvailable.Reset();
            DrainPendingWrites();

            // Checked after a drain pass, not before, so a stop request racing with still-queued work can never
            // strand a caller's PendingWrite.Done unsignaled - see Dispose's remarks.
            if (_stopWriterThread && _pendingWrites.IsEmpty) return;

            _pendingWritesAvailable.Wait();
        }
    }

    /// <summary>
    /// Drains everything currently in <see cref="_pendingWrites"/>, coalescing small writes into capped waves and
    /// routing oversized ones to <see cref="CommitLargeWriteInChunks"/>. Reentrant by design: that method calls
    /// this same routine between each of its own chunks, so a large write's chunking yields to whatever else is
    /// queued instead of monopolizing the writer role for its entire multi-chunk duration - see that method's
    /// remarks for why this matters. Nesting depth is bounded by how many oversized writes happen to be paused
    /// mid-chunk at once, not by total volume processed, since each nested call fully drains before returning.
    /// </summary>
    private void DrainPendingWrites()
    {
        while (_pendingWrites.TryDequeue(out PendingWrite? first))
        {
            if (first.Operations.Count > _maxOperationsPerWave)
            {
                CommitLargeWriteInChunks(first);
                continue;
            }

            List<PendingWrite> wave = [first];
            int opsInWave = first.Operations.Count;
            // An oversized item encountered while coalescing is deferred rather than folded into this
            // wave (it needs CommitLargeWriteInChunks, not this loop) - handled right after this wave
            // commits, before returning to the outer dequeue loop, so overall processing order relative
            // to the queue is preserved.
            PendingWrite? deferredLargeWrite = null;
            while (opsInWave < _maxOperationsPerWave && _pendingWrites.TryDequeue(out PendingWrite? more))
            {
                if (more.Operations.Count > _maxOperationsPerWave)
                {
                    deferredLargeWrite = more;
                    break;
                }

                wave.Add(more);
                opsInWave += more.Operations.Count;
            }

            CommitWaveAndSignal(wave);
            if (deferredLargeWrite is not null) CommitLargeWriteInChunks(deferredLargeWrite);
        }
    }

    private void CommitWaveAndSignal(List<PendingWrite> wave)
    {
        try
        {
            bool durable = false;
            List<(byte[] Key, byte[]? Value)> operations = new(wave.Count == 1 ? wave[0].Operations.Count : _maxOperationsPerWave);
            foreach (PendingWrite w in wave)
            {
                durable |= w.WantsDurability;
                operations.AddRange(w.Operations);
            }

            CommitOperationsWithMapGrowthRetry(operations, durable);
            foreach (PendingWrite w in wave) w.Done.Set();
        }
        catch (Exception ex)
        {
            ExceptionDispatchInfo info = ExceptionDispatchInfo.Capture(ex);
            foreach (PendingWrite w in wave)
            {
                w.Error = info;
                w.Done.Set();
            }
        }
    }

    /// <summary>
    /// Splits a single caller's write batch that by itself exceeds <see cref="ILmdbConfig.MaxOperationsPerWave"/>
    /// into sequential chunks, each committed as its own physical LMDB transaction - see PLAN-LMDB.md "Known
    /// issues". Observed in practice: snap-sync account-tree writes coalesce tens of thousands of entries into
    /// one <c>IWriteBatch</c> before a single <c>Dispose()</c> call, producing one ~40,000-operation physical
    /// commit that (correctly, per <c>_writeCommitGate</c>'s own invariant) blocks every concurrent
    /// <see cref="CreateSnapshot"/> reader for that commit's entire duration - measured at over 150ms average,
    /// plausibly enough to blow past a consensus client's <c>newPayload</c> timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the coalesced-wave path, a failure partway through here is <em>not</em> all-or-nothing: earlier
    /// chunks may already be permanently committed by the time a later one fails, so this caller's write batch
    /// can end up partially applied. Accepted specifically because the callers that produce oversized batches
    /// (snap-sync tree writers - see <c>FlatSnapStateTree.Commit</c>/<c>FlatSnapStorageTree.Commit</c>) already
    /// pass <see cref="WriteFlags.DisableWAL"/>, meaning they already tolerate losing the *entire* batch on a
    /// crash because sync re-fetches and redoes it - the same reasoning extends to tolerating a rare partial
    /// application on a non-crash failure here. This is not assumed to hold for every possible caller of this
    /// plugin in general, only confirmed for the specific ones observed producing oversized batches in practice.
    /// </para>
    /// <para>
    /// Calls <see cref="DrainPendingWrites"/> between chunks - observed in practice without this: the single
    /// writer thread processes one dequeued <see cref="PendingWrite"/> to full completion before ever looking
    /// at the queue again, so "full completion" of one ~40,000-operation write meant its entire ~11-chunk
    /// sequence ran before any OTHER queued write got a turn - including trivial 2-3 operation writes on the
    /// unrelated column, which measured 100-300ms in near-identical clusters despite their own work being
    /// negligible, because they were simply queued behind this write's whole multi-chunk duration. Yielding
    /// here between chunks lets small, latency-sensitive writes interleave instead.
    /// </para>
    /// </remarks>
    private void CommitLargeWriteInChunks(PendingWrite write)
    {
        List<(byte[] Key, byte[]? Value)> operations = write.Operations;
        for (int offset = 0; offset < operations.Count; offset += _maxOperationsPerWave)
        {
            int count = Math.Min(_maxOperationsPerWave, operations.Count - offset);
            List<(byte[] Key, byte[]? Value)> chunk = operations.GetRange(offset, count);
            try
            {
                CommitOperationsWithMapGrowthRetry(chunk, write.WantsDurability);
            }
            catch (Exception ex)
            {
                write.Error = ExceptionDispatchInfo.Capture(ex);
                write.Done.Set();
                return;
            }

            DrainPendingWrites();
        }

        write.Done.Set();
    }

    /// <summary>Doublings attempted before giving up and surfacing <see cref="MDBResultCode.MapFull"/> to callers
    /// as a real error - a safety valve against a pathological loop (e.g. a genuinely full disk under the
    /// map's growth), not a value expected to be hit in practice.</summary>
    private const int MaxMapGrowthAttempts = 24;

    /// <summary>
    /// Applies one drained wave in a single physical write transaction, growing <see cref="LightningEnvironment.MapSize"/>
    /// and retrying the whole wave from scratch if LMDB reports <see cref="MDBResultCode.MapFull"/> - see
    /// PLAN-LMDB.md "MapSize sizing and growth". The verification spike confirmed resizing only throws while a
    /// write transaction is active; the failed transaction is aborted before growing, so no write transaction is
    /// active when <see cref="LightningEnvironment.MapSize"/> is set here.
    /// </summary>
    /// <remarks>
    /// Not yet verified: whether a read transaction opened <em>before</em> a resize (e.g. one already rented from
    /// a <see cref="LmdbDbSnapshot"/> pool) continues to read correctly against pages written after the resize.
    /// The spike only checked that a fresh read transaction opened after a resize sees pre- and post-resize data
    /// correctly, not a long-lived one spanning the resize itself - flagged as a follow-up validation item before
    /// trusting this under real concurrent-snapshot load.
    /// </remarks>
    private void CommitOperationsWithMapGrowthRetry(List<(byte[] Key, byte[]? Value)> operations, bool durable)
    {
        long start = Stopwatch.GetTimestamp();
        TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();
        long pageFaultsBefore = WindowsProcessDiagnostics.CachedPageFaultCount;

        // Per-transaction NoSync (verified honored as of LMDB 1.0 - see PLAN-LMDB.md "Spike findings" #3) is the
        // per-commit durability knob: the environment itself is opened without the env-wide NoSync flag, so a
        // wave where every caller passed WriteFlags.DisableWAL can skip the fsync-equivalent cost, while a wave
        // with at least one durability-wanting caller pays it for the whole physical commit.
        TransactionBeginFlags flags = durable ? TransactionBeginFlags.None : TransactionBeginFlags.NoSync;

        for (int attempt = 0; ; attempt++)
        {
            MDBResultCode? failure;

            // Phase breakdown (begin/apply/commit) - see PLAN-LMDB.md "Known issues": every diagnostic before
            // this one could only report a physical commit's total duration as one opaque number, not which part
            // of it actually dominates.
            long beginStart = Stopwatch.GetTimestamp();

            // The transaction handle must be disposed on every path - including a successful commit - or
            // LightningDB's own open-handle tracking throws "A transaction must be disposed before closing the
            // environment" from a finalizer at some later, unrelated point (observed: it surfaced as an unhandled
            // exception during a completely different test's environment teardown). A `using` here guarantees
            // that regardless of which branch below runs, or which one returns out of the loop entirely.
            using (LightningTransaction tx = _env.BeginTransaction(flags))
            {
                long beginTicks = Stopwatch.GetTimestamp() - beginStart;

                long applyStart = Stopwatch.GetTimestamp();
                failure = ApplyOperations(tx, operations);
                long applyTicks = Stopwatch.GetTimestamp() - applyStart;

                if (failure is null)
                {
                    // The commit itself is the moment a new version becomes "last committed" - see
                    // _writeCommitGate's doc comment for why CreateSnapshot must never observe this mid-flight.
                    // Split into lock-wait (blocked trying to enter _writeCommitGate - this is the lock
                    // CreateSnapshot's whole pool-opening loop also holds, called tens of thousands of times per
                    // 30s in practice, so real contention here would show up as lock-wait, not the native commit
                    // call itself) versus the actual tx.Commit() call once the lock is held - see PLAN-LMDB.md
                    // "Known issues" for why this split was added (an isolated, uncontended benchmark showed
                    // LMDB's own commit cost is flat regardless of concurrently-open reader count, so something
                    // else must explain the live "commit costs more than apply" pattern - this is the next most
                    // likely candidate).
                    MDBResultCode commitRc;
                    long commitStart = Stopwatch.GetTimestamp();
                    long lockAcquiredAt;
                    lock (_writeCommitGate)
                    {
                        lockAcquiredAt = Stopwatch.GetTimestamp();
                        commitRc = tx.Commit();
                    }
                    long commitEnd = Stopwatch.GetTimestamp();
                    long commitTicks = commitEnd - commitStart;
                    long commitLockWaitTicks = lockAcquiredAt - commitStart;
                    long commitCallTicks = commitEnd - lockAcquiredAt;

                    if (commitRc == MDBResultCode.Success)
                    {
                        long totalTicks = Stopwatch.GetTimestamp() - start;
                        Interlocked.Increment(ref _writeWaveCount);
                        Interlocked.Add(ref _writeWaveElapsedTicks, totalTicks);
                        Interlocked.Add(ref _writeWaveOpsCount, operations.Count);
                        Interlocked.Add(ref _writeBeginElapsedTicks, beginTicks);
                        Interlocked.Add(ref _writeApplyElapsedTicks, applyTicks);
                        Interlocked.Add(ref _writeCommitElapsedTicks, commitTicks);
                        Interlocked.Add(ref _writeCommitLockWaitElapsedTicks, commitLockWaitTicks);
                        Interlocked.Add(ref _writeCommitCallElapsedTicks, commitCallTicks);

                        double totalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
                        if (totalMs >= SlowOperationThresholdMs && _logger.IsWarn)
                        {
                            double beginMs = beginTicks * 1000.0 / Stopwatch.Frequency;
                            double applyMs = applyTicks * 1000.0 / Stopwatch.Frequency;
                            double commitLockWaitMs = commitLockWaitTicks * 1000.0 / Stopwatch.Frequency;
                            double commitCallMs = commitCallTicks * 1000.0 / Stopwatch.Frequency;
                            double gcPauseDuringMs = (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds;
                            long pageFaultsDuring = WindowsProcessDiagnostics.ReadNow() - pageFaultsBefore;
                            _logger.Warn($"Lmdb '{Name}' physical commit took {totalMs:F1}ms ({operations.Count} ops) - " +
                                $"begin={beginMs:F1}ms, apply={applyMs:F1}ms, commit lock-wait={commitLockWaitMs:F1}ms, " +
                                $"commit call={commitCallMs:F1}ms, GC pause during: {gcPauseDuringMs:F1}ms, " +
                                $"page faults during: {pageFaultsDuring:N0} (Windows only, 0 elsewhere).");
                        }

                        return;
                    }

                    failure = commitRc;
                }
                else
                {
                    tx.Abort();
                }
            }

            if (failure != MDBResultCode.MapFull || attempt >= MaxMapGrowthAttempts)
            {
                throw new InvalidOperationException($"LMDB write failed with {failure} after {attempt + 1} attempt(s).");
            }

            lock (_writeCommitGate)
            {
                _env.MapSize *= 2;
            }

            Interlocked.Increment(ref _mapGrowthCount);
        }
    }

    /// <summary>Applies every operation against <paramref name="tx"/>, stopping at the first failing result code
    /// (currently only <see cref="MDBResultCode.MapFull"/> is expected in practice) so the whole set can be
    /// retried atomically rather than left half-applied in an aborted transaction.</summary>
    private MDBResultCode? ApplyOperations(LightningTransaction tx, List<(byte[] Key, byte[]? Value)> operations)
    {
        if (_databases.Length == 1) return ApplyOperationsToDatabase(tx, _databases[0], operations);

        // A wave coalesces operations from several concurrent callers (see CommitWaveAndSignal), which may
        // belong to different partitions - each partition is an independent, disjoint keyspace in its own named
        // database, so grouping them has no correctness effect on cross-partition ordering. Only same-partition
        // relative order matters (e.g. a delete followed by a put to the same key), and that is preserved
        // automatically here: List<T>.Add always appends in the original iteration order, and every operation
        // for a given key always lands in the same partition's list (GetPartitionIndex is a pure function of the
        // key), so two operations on the same key can never end up reordered relative to each other.
        Dictionary<int, List<(byte[] Key, byte[]? Value)>> byPartition = [];
        foreach ((byte[] key, byte[]? value) in operations)
        {
            int index = GetPartitionIndex(key, _databases.Length);
            if (!byPartition.TryGetValue(index, out List<(byte[] Key, byte[]? Value)>? partitionOperations))
            {
                byPartition[index] = partitionOperations = [];
            }

            partitionOperations.Add((key, value));
        }

        foreach ((int index, List<(byte[] Key, byte[]? Value)> partitionOperations) in byPartition)
        {
            MDBResultCode? failure = ApplyOperationsToDatabase(tx, _databases[index], partitionOperations);
            if (failure is not null) return failure;
        }

        return null;
    }

    private MDBResultCode? ApplyOperationsToDatabase(LightningTransaction tx, LightningDatabase database, List<(byte[] Key, byte[]? Value)> operations)
    {
        bool canAppend = CanUseAppendData(tx, database, operations);

        long applyGroupStart = Stopwatch.GetTimestamp();
        foreach ((byte[] key, byte[]? value) in operations)
        {
            MDBResultCode rc;
            if (value is null)
            {
                rc = DeleteAllowingMissing(tx, database, key);
            }
            else
            {
                Interlocked.Increment(ref _totalPutCount);
                if (canAppend) Interlocked.Increment(ref _appendedPutCount);
                rc = tx.Put(database, key, value, canAppend ? PutOptions.AppendData : PutOptions.None);
            }

            // A failed group's partial timing represents neither bucket faithfully (MapFull aborts and retries
            // the whole wave from scratch anyway), so it is deliberately not recorded here.
            if (rc != MDBResultCode.Success) return rc;
        }

        long applyGroupTicks = Stopwatch.GetTimestamp() - applyGroupStart;
        if (canAppend)
        {
            Interlocked.Add(ref _appendedApplyElapsedTicks, applyGroupTicks);
            Interlocked.Add(ref _appendedApplyOpsCount, operations.Count);
        }
        else
        {
            Interlocked.Add(ref _nonAppendedApplyElapsedTicks, applyGroupTicks);
            Interlocked.Add(ref _nonAppendedApplyOpsCount, operations.Count);
        }

        return null;
    }

    /// <summary>
    /// Whether every Put in <paramref name="operations"/> (Deletes don't add a key, so they cannot break the
    /// invariant below and are ignored here) can safely use <see cref="PutOptions.AppendData"/> (<c>MDB_APPEND</c>)
    /// for this transaction - see PLAN-LMDB.md "Known issues" for the full reasoning behind this. LMDB's own
    /// semantics (LightningDB.xml: "append the given key/data pair to the end of the database... no key
    /// comparisons are performed") require every appended key to be strictly greater than whatever is already
    /// the database's actual current maximum key - not merely greater than its neighbours within this one batch.
    /// Checked here directly against <paramref name="tx"/>'s own view of the database (a single cheap O(log n)
    /// cursor seek to the last key) rather than assumed from caller intent, which makes this self-correcting
    /// regardless of *why* a batch might not qualify: snap sync's bulk range-sync phase genuinely does advance
    /// the keyspace frontier monotonically (each account/storage range covers hashes strictly beyond the last),
    /// but several coalesced callers' operations commingled in one wave (see <see cref="CommitWaveAndSignal"/>),
    /// two concurrent range responses completing out of request order, or snap sync's later healing phase
    /// patching already-synced keys out of order, would all violate it - and LMDB's documented failure mode for
    /// misuse here is silent data corruption, not a thrown error, so this must never guess.
    /// </summary>
    private bool CanUseAppendData(LightningTransaction tx, LightningDatabase database, List<(byte[] Key, byte[]? Value)> operations)
    {
        byte[]? firstPutKey = null;
        byte[]? previousPutKey = null;

        foreach ((byte[] key, byte[]? value) in operations)
        {
            if (value is null) continue;
            if (previousPutKey is not null && Bytes.BytesComparer.Compare(key, previousPutKey) <= 0) return false;
            firstPutKey ??= key;
            previousPutKey = key;
        }

        if (firstPutKey is null) return false;

        using LightningCursor cursor = tx.CreateCursor(database);
        (MDBResultCode rc, MDBValue lastKey, MDBValue _) = cursor.Last();
        return rc != MDBResultCode.Success || Bytes.BytesComparer.Compare(firstPutKey, lastKey.CopyToNewArray()) > 0;
    }

    /// <summary>Deleting a key that is not present is not an error for <see cref="IWriteOnlyKeyValueStore.Set"/>'s
    /// contract (unlike LMDB's own <c>Delete</c>, which reports <see cref="MDBResultCode.NotFound"/>).</summary>
    private MDBResultCode DeleteAllowingMissing(LightningTransaction tx, LightningDatabase database, byte[] key)
    {
        MDBResultCode rc = tx.Delete(database, key);
        return rc == MDBResultCode.NotFound ? MDBResultCode.Success : rc;
    }

    /// <summary>
    /// One caller's coalesced write, plus the signal it waits on. Uses <see cref="ManualResetEventSlim"/> rather
    /// than a <see cref="System.Threading.Tasks.TaskCompletionSource"/> deliberately - mirrors BTDB bug #6's fix:
    /// a plain wait handle signals its single waiter directly, without a <c>TaskCompletionSource</c>'s
    /// completion machinery (debugger-notification checks, exception-marshaling setup, and a thread-pool round
    /// trip to avoid running the writer thread's own continuation inline).
    /// </summary>
    private sealed class PendingWrite(List<(byte[] Key, byte[]? Value)> operations, bool wantsDurability)
    {
        public List<(byte[] Key, byte[]? Value)> Operations => operations;
        public bool WantsDurability => wantsDurability;
        public ManualResetEventSlim Done { get; } = new();
        public ExceptionDispatchInfo? Error;
    }

    private sealed class LmdbWriteBatch(LmdbDb owner) : IWriteBatch
    {
        private readonly List<(byte[] Key, byte[]? Value)> _operations = [];
        private bool _wantsDurability;
        private bool _disposed;

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (!flags.HasFlag(WriteFlags.DisableWAL)) _wantsDurability = true;
            _operations.Add((key.ToArray(), value));
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            if (!flags.HasFlag(WriteFlags.DisableWAL)) _wantsDurability = true;
            _operations.Add((key.ToArray(), value.ToArray()));
        }

        public void Remove(ReadOnlySpan<byte> key) => _operations.Add((key.ToArray(), null));

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            throw new NotSupportedException("LMDB has no native merge-operator equivalent to RocksDB's.");

        public void Clear()
        {
            _operations.Clear();
            _wantsDurability = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // An empty batch has nothing to commit - skip the round trip through the write coordinator entirely.
            if (_operations.Count > 0) owner.SubmitWrite(_operations, _wantsDurability);
        }
    }

    /// <summary>
    /// Point-in-time view over a pool of LMDB read-only transactions, all pinned to the same committed version
    /// (see <see cref="CreateSnapshot"/>). Every accessor rents one transaction from the pool for the duration of
    /// one call (or, for <see cref="GetViewBetween"/>, for the returned view's whole lifetime), uses it
    /// exclusively, and returns it - so no two threads ever touch the same transaction/cursor at once.
    /// </summary>
    /// <remarks>
    /// Mirrors BTDB's <c>BtdbDbSnapshot</c>, which replaced an earlier design sharing one transaction (guarded by
    /// a lock) across every accessor call after that pattern both corrupted under concurrent multi-threaded
    /// cursor use (BTDB bug #7) and became the dominant cost of the read path it protected. The verification
    /// spike's cross-thread test (PLAN-LMDB.md "Spike findings" #2) did not throw when reusing one transaction
    /// from a different thread, but explicitly does not prove that pattern is safe under genuine concurrency -
    /// only that LMDB doesn't guard against it with an exception, which is exactly BTDB bug #7's shape (silent
    /// corruption, not a thrown error). A pool of independently-opened transactions sidesteps the question
    /// entirely: each pooled transaction has its own independent cursor state, so once a thread has exclusively
    /// rented one, it needs no synchronization to use it.
    /// </remarks>
    private sealed class LmdbDbSnapshot(LightningTransaction[] pool, LightningDatabase[] databases, LmdbDb owner) : IKeyValueStoreSnapshot, ISortedKeyValueStore
    {
        private readonly ConcurrentQueue<LightningTransaction> _available = new(pool);
        private readonly SemaphoreSlim _availablePermits = new(pool.Length, pool.Length);

        /// <summary>Blocks only when every pooled transaction is currently rented out - i.e. concurrent demand
        /// exceeds the pool size - rather than on every call as a single shared transaction's lock would.</summary>
        private LightningTransaction Rent()
        {
            _availablePermits.Wait();
            bool dequeued = _available.TryDequeue(out LightningTransaction? tx);
            System.Diagnostics.Debug.Assert(dequeued, "A permit was acquired but the queue was empty - pool bookkeeping is broken.");
            return tx!;
        }

        private void Return(LightningTransaction tx)
        {
            _available.Enqueue(tx);
            _availablePermits.Release();
        }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            LightningTransaction tx = Rent();
            try
            {
                LightningDatabase database = databases[GetPartitionIndex(key, databases.Length)];
                (MDBResultCode rc, _, MDBValue value) = tx.Get(database, key);
                return rc == MDBResultCode.Success ? value.CopyToNewArray() : null;
            }
            finally
            {
                Return(tx);
            }
        }

        public byte[]? FirstKey
        {
            get
            {
                LightningTransaction tx = Rent();
                try
                {
                    foreach (LightningDatabase database in databases)
                    {
                        using LightningCursor cursor = tx.CreateCursor(database);
                        (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.First();
                        if (rc == MDBResultCode.Success) return key.CopyToNewArray();
                    }

                    return null;
                }
                finally
                {
                    Return(tx);
                }
            }
        }

        public byte[]? LastKey
        {
            get
            {
                LightningTransaction tx = Rent();
                try
                {
                    for (int i = databases.Length - 1; i >= 0; i--)
                    {
                        using LightningCursor cursor = tx.CreateCursor(databases[i]);
                        (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.Last();
                        if (rc == MDBResultCode.Success) return key.CopyToNewArray();
                    }

                    return null;
                }
                finally
                {
                    Return(tx);
                }
            }
        }

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            // Held for the view's whole iteration lifetime, not just this call - returned via the merged view's
            // own Dispose (returnToPool), since the view keeps navigating this exact transaction across many
            // MoveNext calls.
            byte[] first = firstKeyInclusive.ToArray();
            byte[] last = lastKeyExclusive.ToArray();
            (int PartitionIndex, byte[] EffectiveFirst, byte[] EffectiveLast)[] parts = GetIntersectingPartitions(databases.Length, first, last);

            LightningTransaction tx = Rent();
            (int PartitionIndex, LmdbSortedView View)[] views = new (int, LmdbSortedView)[parts.Length];
            int built = 0;
            try
            {
                for (; built < parts.Length; built++)
                {
                    LightningCursor cursor = tx.CreateCursor(databases[parts[built].PartitionIndex]);
                    views[built] = (parts[built].PartitionIndex, new LmdbSortedView(tx, cursor, parts[built].EffectiveFirst, parts[built].EffectiveLast, ownsTransaction: false));
                }

                return new LmdbMultiPartitionSortedView(views, databases.Length, tx, ownsTransaction: false, Return);
            }
            catch
            {
                for (int i = 0; i < built; i++) views[i].View.Dispose();
                // Must go back to the pool, not be disposed - disposing a still-valid pooled transaction here
                // would permanently shrink the pool below SnapshotReaderPoolSize for this snapshot's lifetime.
                Return(tx);
                throw;
            }
        }

        public void Dispose()
        {
            // Every rented transaction must come back before any of them is torn down - a lease still
            // outstanding here would mean a caller is still using it, so wait rather than risk disposing a
            // transaction mid-use.
            for (int i = 0; i < pool.Length; i++) _availablePermits.Wait();
            foreach (LightningTransaction tx in pool) tx.Dispose();
            _availablePermits.Dispose();
            Interlocked.Add(ref owner._aliveSnapshotPoolReaders, -pool.Length);
        }
    }

    /// <summary>
    /// <see cref="ISortedView"/> over an LMDB cursor. Both bounds are re-checked after every move rather than
    /// enforced natively, mirroring BTDB's equivalent view - LMDB has no native iterate-bound concept either.
    /// </summary>
    /// <param name="ownsTransaction">
    /// Whether <see cref="Dispose"/> should dispose <paramref name="tx"/> outright. <see langword="true"/> for a
    /// view opened directly off <see cref="LmdbDb.GetViewBetween"/>, which owns a dedicated transaction for that
    /// one view; <see langword="false"/> for a view opened off <see cref="LmdbDbSnapshot"/>'s pool, whose
    /// transaction must be returned to the pool instead (via <paramref name="returnToPool"/>) so other readers can
    /// reuse it, not disposed.
    /// </param>
    private sealed class LmdbSortedView(LightningTransaction tx, LightningCursor cursor, byte[] firstKeyInclusive, byte[] lastKeyExclusive, bool ownsTransaction, Action<LightningTransaction>? returnToPool = null) : ISortedView
    {
        private bool _started;
        private bool _exhausted;
        private byte[]? _currentKey;
        private byte[]? _currentValue;

        /// <summary>
        /// Positions on the greatest key &lt;= <paramref name="value"/> (the "floor"). LMDB's cursor primitive,
        /// <see cref="LightningCursor.SetRange"/>, finds the smallest key &gt;= a value (the "ceiling") - the
        /// opposite direction - so the floor is derived from it: an exact match is trivially its own floor;
        /// otherwise stepping one key back from the ceiling gives the floor, and if there is no ceiling at all
        /// (every key is smaller than <paramref name="value"/>), the floor is simply the greatest key overall.
        /// </summary>
        public bool StartBefore(ReadOnlySpan<byte> value)
        {
            if (_started)
                throw new InvalidOperationException($"{nameof(StartBefore)} can only be called before starting iteration.");
            _started = true;

            byte[] seekValue = value.ToArray();
            MDBResultCode rangeRc = cursor.SetRange(seekValue);
            bool positioned;
            if (rangeRc == MDBResultCode.Success)
            {
                (MDBResultCode currentRc, MDBValue currentKey, MDBValue _) = cursor.GetCurrent();
                positioned = currentRc == MDBResultCode.Success &&
                    (Bytes.BytesComparer.Compare(currentKey.CopyToNewArray(), seekValue) == 0 ||
                     cursor.Previous().Item1 == MDBResultCode.Success);
            }
            else
            {
                positioned = cursor.Last().Item1 == MDBResultCode.Success;
            }

            if (!positioned)
            {
                _exhausted = true;
                return false;
            }

            return LoadCurrentIfInBounds();
        }

        public bool MoveNext()
        {
            if (_exhausted) return false;

            bool moved = _started ? cursor.Next().Item1 == MDBResultCode.Success : PositionAtFirst();
            _started = true;

            if (!moved)
            {
                _exhausted = true;
                return false;
            }

            return LoadCurrentIfInBounds();
        }

        /// <summary>Positions the cursor at the smallest key >= <c>firstKeyInclusive</c> - <see cref="LightningCursor.SetRange"/>
        /// is exactly that "ceiling" primitive, unlike the floor <see cref="StartBefore"/> needs.</summary>
        private bool PositionAtFirst() => cursor.SetRange(firstKeyInclusive) == MDBResultCode.Success;

        private bool LoadCurrentIfInBounds()
        {
            (MDBResultCode rc, MDBValue keyValue, MDBValue valueValue) = cursor.GetCurrent();
            if (rc != MDBResultCode.Success)
            {
                _exhausted = true;
                _currentKey = null;
                _currentValue = null;
                return false;
            }

            byte[] key = keyValue.CopyToNewArray();
            if (Bytes.BytesComparer.Compare(key, firstKeyInclusive) < 0 ||
                Bytes.BytesComparer.Compare(key, lastKeyExclusive) >= 0)
            {
                _exhausted = true;
                _currentKey = null;
                _currentValue = null;
                return false;
            }

            _currentKey = key;
            _currentValue = valueValue.CopyToNewArray();
            return true;
        }

        public ReadOnlySpan<byte> CurrentKey => _currentKey;
        public ReadOnlySpan<byte> CurrentValue => _currentValue;

        public void Dispose()
        {
            cursor.Dispose();
            if (ownsTransaction) tx.Dispose();
            else returnToPool?.Invoke(tx);
        }
    }

    /// <summary>
    /// Merges <paramref name="parts"/> - one <see cref="LmdbSortedView"/> per partition that intersects the
    /// original query range, already clipped to that intersection (see <see cref="GetIntersectingPartitions"/>)
    /// - into a single <see cref="ISortedView"/>. No k-way merge is needed: partitions are disjoint, contiguous
    /// key ranges assigned in ascending order (see <see cref="_databases"/>'s remarks), so simply exhausting
    /// <paramref name="parts"/> in array order visits every key in the same order a single flat keyspace would.
    /// Reduces to exactly <see cref="LmdbSortedView"/>'s own behavior when <paramref name="parts"/> has one
    /// entry, which is always true in the common, non-partitioned case.
    /// </summary>
    /// <remarks>
    /// <see cref="StartBefore"/> is the one case genuinely specific to merging: the floor of a seek value can
    /// only live in the value's own partition or a strictly lower-indexed one (every higher partition's entire
    /// key range is greater than the value, by construction), so this walks backward from the value's own
    /// partition rather than trying every part - trying a higher partition would incorrectly report a floor that
    /// is actually greater than the seek value, since <see cref="LmdbSortedView.StartBefore"/> only checks its
    /// own clipped bounds, not the seek value itself, once it has positioned the cursor. No production call site
    /// in this codebase exercises <see cref="StartBefore"/> against a partitioned column in practice (every real
    /// caller of <c>GetViewBetween</c> on Account/Storage only ever calls <see cref="MoveNext"/> from the start -
    /// see PLAN-LMDB.md "Known issues"), but this is still implemented correctly rather than left as a known gap.
    /// </remarks>
    private sealed class LmdbMultiPartitionSortedView(
        (int PartitionIndex, LmdbSortedView View)[] parts,
        int totalPartitionCount,
        LightningTransaction tx,
        bool ownsTransaction,
        Action<LightningTransaction>? returnToPool = null) : ISortedView
    {
        private int _currentIndex = -1;
        private bool _started;
        private bool _exhausted;

        public bool StartBefore(ReadOnlySpan<byte> value)
        {
            if (_started)
                throw new InvalidOperationException($"{nameof(StartBefore)} can only be called before starting iteration.");
            _started = true;

            int valuePartition = GetPartitionIndex(value, totalPartitionCount);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (parts[i].PartitionIndex > valuePartition) continue;
                if (parts[i].View.StartBefore(value))
                {
                    _currentIndex = i;
                    return true;
                }
            }

            _exhausted = true;
            return false;
        }

        public bool MoveNext()
        {
            if (_exhausted) return false;
            if (!_started)
            {
                _started = true;
                _currentIndex = 0;
            }

            while (_currentIndex < parts.Length)
            {
                if (parts[_currentIndex].View.MoveNext()) return true;
                _currentIndex++;
            }

            _exhausted = true;
            return false;
        }

        public ReadOnlySpan<byte> CurrentKey => _currentIndex >= 0 && _currentIndex < parts.Length ? parts[_currentIndex].View.CurrentKey : default;
        public ReadOnlySpan<byte> CurrentValue => _currentIndex >= 0 && _currentIndex < parts.Length ? parts[_currentIndex].View.CurrentValue : default;

        public void Dispose()
        {
            foreach ((int _, LmdbSortedView view) in parts) view.Dispose();
            if (ownsTransaction) tx.Dispose();
            else returnToPool?.Invoke(tx);
        }
    }
}
