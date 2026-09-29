// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using LightningDB;
using Nethermind.Core;
using Nethermind.Core.Extensions;

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
    private readonly LightningDatabase _database;
    private bool _disposed;

    /// <summary>
    /// Guards two things that must never interleave: (1) the moment a write's <c>Commit()</c> becomes visible to
    /// new read-only transactions against <see cref="CreateSnapshot"/> opening its reader pool (see that
    /// method's remarks - mirrors BTDB's <c>_writeCommitGate</c>), and (2) growing <see cref="LightningEnvironment.MapSize"/>
    /// on a <see cref="MDBResultCode.MapFull"/> retry, which the verification spike (see PLAN-LMDB.md "Spike
    /// findings") confirmed only throws when a write transaction is active - by construction there is none while
    /// this lock is held outside of <see cref="DrainIfLeader"/>'s own write-transaction section.
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

    public string Name { get; }

    public LmdbDb(string name, string path, long initialMapSizeBytes, int maxReaders)
    {
        Name = name;
        _path = path;
        Directory.CreateDirectory(path);

        // NoThreadLocalStorage: CreateSnapshot's reader pool hands pooled read transactions out via rent/return,
        // so a given transaction's using thread can legitimately change between rentals - the default
        // TLS-slot-per-thread reader binding is not designed for that pattern (see PLAN-LMDB.md spike finding 2).
        // MaxReaders defaults to only 126 in LMDB itself, which CreateSnapshot's pool of
        // Environment.ProcessorCount readers per snapshot burns through with just a handful of concurrently-alive
        // snapshots on a many-core box (observed in practice: MDB_READERS_FULL) - see BeginReadOnlyTransaction.
        _env = new LightningEnvironment(path, new EnvironmentConfiguration { MapSize = initialMapSizeBytes, MaxReaders = maxReaders });
        _env.Open(EnvironmentOpenFlags.NoThreadLocalStorage);

        using LightningTransaction tx = _env.BeginTransaction();
        _database = tx.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create }, closeOnDispose: false);
        tx.Commit();
    }

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
        (MDBResultCode rc, _, MDBValue value) = tx.Get(_database, key);
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
                (MDBResultCode rc, _, MDBValue value) = tx.Get(_database, key);
                result[i] = new KeyValuePair<byte[], byte[]?>(key, rc == MDBResultCode.Success ? value.CopyToNewArray() : null);
            }

            return result;
        }
    }

    public IWriteBatch StartWriteBatch() => new LmdbWriteBatch(this);

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
    {
        // LMDB's own index is a B+tree, so it is always ordered - the flag has nothing extra to opt into.
        using LightningTransaction tx = BeginReadOnlyTransaction();
        using LightningCursor cursor = tx.CreateCursor(_database);
        (MDBResultCode rc, MDBValue key, MDBValue value) = cursor.First();
        while (rc == MDBResultCode.Success)
        {
            yield return new KeyValuePair<byte[], byte[]>(key.CopyToNewArray(), value.CopyToNewArray());
            (rc, key, value) = cursor.Next();
        }
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        using LightningTransaction tx = BeginReadOnlyTransaction();
        using LightningCursor cursor = tx.CreateCursor(_database);
        (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.First();
        while (rc == MDBResultCode.Success)
        {
            yield return key.CopyToNewArray();
            (rc, key, _) = cursor.Next();
        }
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        using LightningTransaction tx = BeginReadOnlyTransaction();
        using LightningCursor cursor = tx.CreateCursor(_database);
        (MDBResultCode rc, MDBValue _, MDBValue value) = cursor.First();
        while (rc == MDBResultCode.Success)
        {
            yield return value.CopyToNewArray();
            (rc, _, value) = cursor.Next();
        }
    }

    public byte[]? FirstKey
    {
        get
        {
            using LightningTransaction tx = BeginReadOnlyTransaction();
            using LightningCursor cursor = tx.CreateCursor(_database);
            (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.First();
            return rc == MDBResultCode.Success ? key.CopyToNewArray() : null;
        }
    }

    public byte[]? LastKey
    {
        get
        {
            using LightningTransaction tx = BeginReadOnlyTransaction();
            using LightningCursor cursor = tx.CreateCursor(_database);
            (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.Last();
            return rc == MDBResultCode.Success ? key.CopyToNewArray() : null;
        }
    }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
    {
        LightningTransaction tx = BeginReadOnlyTransaction();
        try
        {
            LightningCursor cursor = tx.CreateCursor(_database);
            return new LmdbSortedView(tx, cursor, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray(), ownsTransaction: true);
        }
        catch
        {
            tx.Dispose();
            throw;
        }
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
    /// they are opened back-to-back under <see cref="_writeCommitGate"/>, the same lock <see cref="DrainIfLeader"/>
    /// holds around its own <c>Commit()</c> call: this guarantees no commit can become visible partway through
    /// the loop below, which would otherwise split the pool across two versions.
    /// </remarks>
    public IKeyValueStoreSnapshot CreateSnapshot()
    {
        long start = Stopwatch.GetTimestamp();
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

        Interlocked.Increment(ref _createSnapshotCount);
        Interlocked.Add(ref _createSnapshotElapsedTicks, Stopwatch.GetTimestamp() - start);
        return new LmdbDbSnapshot(pool, _database);
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
            return tx.GetEntriesCount(_database);
        }
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
        long MapGrowthCount);

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
        return new DiagnosticsSnapshot(
            getCount, AvgMicros(Interlocked.Exchange(ref _getElapsedTicks, 0), getCount),
            createSnapshotCount, AvgMicros(Interlocked.Exchange(ref _createSnapshotElapsedTicks, 0), createSnapshotCount),
            writeWaveCount, AvgMicros(Interlocked.Exchange(ref _writeWaveElapsedTicks, 0), writeWaveCount),
            writeWaveCount == 0 ? 0 : (double)writeWaveOpsCount / writeWaveCount,
            Interlocked.Exchange(ref _mapGrowthCount, 0));

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
        _database.Dispose();
        _env.Dispose();
    }

    private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();
    private int _writerActive;

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
    /// drained wave (see <see cref="DrainIfLeader"/>) removes the per-caller acquire/park/wake overhead without
    /// raising the single-writer mutation ceiling, which was already fully serialized either way.
    /// </remarks>
    private void SubmitWrite(List<(byte[] Key, byte[]? Value)> operations, bool wantsDurability)
    {
        PendingWrite write = new(operations, wantsDurability);
        _pendingWrites.Enqueue(write);
        DrainIfLeader();
        write.Done.Wait();
        write.Done.Dispose();
        write.Error?.Throw();
    }

    /// <summary>
    /// Becomes the sole writer for as long as there is queued work: repeatedly drains everything currently in
    /// <see cref="_pendingWrites"/> into one physical LMDB write transaction ("wave"), commits it once, and
    /// signals every caller whose operations were part of that wave. If another thread is already draining, this
    /// returns immediately - that thread's own loop will pick up the work this call just enqueued, since the
    /// enqueue always happens before this check.
    /// </summary>
    private void DrainIfLeader()
    {
        while (true)
        {
            if (Interlocked.CompareExchange(ref _writerActive, 1, 0) != 0) return;

            try
            {
                while (_pendingWrites.TryDequeue(out PendingWrite? first))
                {
                    List<PendingWrite> wave = [first];
                    while (_pendingWrites.TryDequeue(out PendingWrite? more)) wave.Add(more);

                    try
                    {
                        CommitWaveWithMapGrowthRetry(wave);
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
            }
            finally
            {
                Interlocked.Exchange(ref _writerActive, 0);
            }

            // Closes a race: a caller may have enqueued work in the narrow window between this thread's last
            // TryDequeue returning false and the Exchange above actually running, in which case that caller's
            // own DrainIfLeader call saw _writerActive still set and returned without draining anything. Go
            // around again rather than leaving that work stranded until an unrelated future write picks it up.
            if (_pendingWrites.IsEmpty) return;
        }
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
    private void CommitWaveWithMapGrowthRetry(List<PendingWrite> wave)
    {
        long start = Stopwatch.GetTimestamp();
        bool durable = false;
        int opsCount = 0;
        foreach (PendingWrite w in wave)
        {
            durable |= w.WantsDurability;
            opsCount += w.Operations.Count;
        }

        // Per-transaction NoSync (verified honored as of LMDB 1.0 - see PLAN-LMDB.md "Spike findings" #3) is the
        // per-commit durability knob: the environment itself is opened without the env-wide NoSync flag, so a
        // wave where every caller passed WriteFlags.DisableWAL can skip the fsync-equivalent cost, while a wave
        // with at least one durability-wanting caller pays it for the whole physical commit.
        TransactionBeginFlags flags = durable ? TransactionBeginFlags.None : TransactionBeginFlags.NoSync;

        for (int attempt = 0; ; attempt++)
        {
            MDBResultCode? failure;

            // The transaction handle must be disposed on every path - including a successful commit - or
            // LightningDB's own open-handle tracking throws "A transaction must be disposed before closing the
            // environment" from a finalizer at some later, unrelated point (observed: it surfaced as an unhandled
            // exception during a completely different test's environment teardown). A `using` here guarantees
            // that regardless of which branch below runs, or which one returns out of the loop entirely.
            using (LightningTransaction tx = _env.BeginTransaction(flags))
            {
                failure = ApplyWave(tx, wave);

                if (failure is null)
                {
                    // The commit itself is the moment a new version becomes "last committed" - see
                    // _writeCommitGate's doc comment for why CreateSnapshot must never observe this mid-flight.
                    MDBResultCode commitRc;
                    lock (_writeCommitGate)
                    {
                        commitRc = tx.Commit();
                    }

                    if (commitRc == MDBResultCode.Success)
                    {
                        Interlocked.Increment(ref _writeWaveCount);
                        Interlocked.Add(ref _writeWaveElapsedTicks, Stopwatch.GetTimestamp() - start);
                        Interlocked.Add(ref _writeWaveOpsCount, opsCount);
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

    /// <summary>Applies every operation in <paramref name="wave"/> against <paramref name="tx"/>, stopping at the
    /// first failing result code (currently only <see cref="MDBResultCode.MapFull"/> is expected in practice) so
    /// the whole wave can be retried atomically rather than left half-applied in an aborted transaction.</summary>
    private MDBResultCode? ApplyWave(LightningTransaction tx, List<PendingWrite> wave)
    {
        foreach (PendingWrite w in wave)
        {
            foreach ((byte[] key, byte[]? value) in w.Operations)
            {
                MDBResultCode rc = value is null
                    ? DeleteAllowingMissing(tx, key)
                    : tx.Put(_database, key, value, PutOptions.None);

                if (rc != MDBResultCode.Success) return rc;
            }
        }

        return null;
    }

    /// <summary>Deleting a key that is not present is not an error for <see cref="IWriteOnlyKeyValueStore.Set"/>'s
    /// contract (unlike LMDB's own <c>Delete</c>, which reports <see cref="MDBResultCode.NotFound"/>).</summary>
    private MDBResultCode DeleteAllowingMissing(LightningTransaction tx, byte[] key)
    {
        MDBResultCode rc = tx.Delete(_database, key);
        return rc == MDBResultCode.NotFound ? MDBResultCode.Success : rc;
    }

    /// <summary>
    /// One caller's coalesced write, plus the signal it waits on. Uses <see cref="ManualResetEventSlim"/> rather
    /// than a <see cref="System.Threading.Tasks.TaskCompletionSource"/> deliberately - mirrors BTDB bug #6's fix:
    /// a plain wait handle signals its single waiter directly, without a <c>TaskCompletionSource</c>'s
    /// completion machinery (debugger-notification checks, exception-marshaling setup, and a thread-pool round
    /// trip to avoid running the leader's own continuation inline).
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
    private sealed class LmdbDbSnapshot(LightningTransaction[] pool, LightningDatabase database) : IKeyValueStoreSnapshot, ISortedKeyValueStore
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
                    using LightningCursor cursor = tx.CreateCursor(database);
                    (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.First();
                    return rc == MDBResultCode.Success ? key.CopyToNewArray() : null;
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
                    using LightningCursor cursor = tx.CreateCursor(database);
                    (MDBResultCode rc, MDBValue key, MDBValue _) = cursor.Last();
                    return rc == MDBResultCode.Success ? key.CopyToNewArray() : null;
                }
                finally
                {
                    Return(tx);
                }
            }
        }

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            // Held for the view's whole iteration lifetime, not just this call - returned via the view's own
            // Dispose (returnToPool), since the view keeps navigating this exact transaction across many
            // MoveNext calls.
            LightningTransaction tx = Rent();
            try
            {
                LightningCursor cursor = tx.CreateCursor(database);
                return new LmdbSortedView(tx, cursor, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray(), ownsTransaction: false, Return);
            }
            catch
            {
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
}
