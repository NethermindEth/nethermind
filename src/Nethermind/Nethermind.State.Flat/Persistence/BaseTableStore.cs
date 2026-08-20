// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// The arena base tier behind <see cref="ArenaBasePersistence"/>: prefix-sharded immutable
/// <c>SortedTable</c>s, one per shard, each in its own mmap arena file, plus the shard registry persisted
/// in the flat DB's Metadata column.
/// </summary>
/// <remarks>
/// <para>
/// Write batches only ever touch the RocksDB Account/Storage columns (the overlay); a <b>fold</b>
/// materializes the overlay∪base merge per overlay-touched shard into a NEW shard file, fsyncs it, then
/// commits ONE RocksDB batch {registry pointer updates + deletes of the folded overlay keys}. A crash on
/// either side of that commit is benign: the registry always points at complete, fsynced tables, and
/// startup sweeps table files the registry does not reference.
/// </para>
/// <para>
/// Readers lease an immutable <see cref="BaseTableView"/>; a fold swaps in a new view and drops the
/// store's own lease on replaced files, which are deleted once the last reader lease drains
/// (<see cref="ArenaFile"/> refcounting). On shutdown live files are flagged
/// <see cref="ArenaFile.PersistOnShutdown"/> so they survive to the next session.
/// </para>
/// </remarks>
internal sealed class BaseTableStore : IDisposable
{
    internal const int DefaultAccountShardCount = 256;
    internal const int DefaultStorageShardCount = 4096;

    /// <summary>
    /// Overlay deletion marker. Deletions cannot be plain RocksDB deletes — the key may still exist in a
    /// base shard table, and a missing overlay row falls through to it — so they are written as this
    /// sentinel value instead.
    /// </summary>
    /// <remarks>
    /// The length is what makes it unambiguous under <em>both</em> slot encodings, so the arena works on
    /// legacy raw-encoded DBs too: a raw slot value is at most <see cref="SlotValue.ByteCount"/> stripped
    /// bytes, and the only RLP slot value this long is a full 32-byte string, which opens with 0xa0 rather
    /// than <see cref="OverlayTombstone"/>. Accounts are slim-RLP lists (first byte ≥ 0xc0, and the slim
    /// encoder never reaches 0xff) at any length. Sizing it to the slot read buffer also keeps a tombstone
    /// readable through the existing <see cref="BaseFlatPersistence.RlpSlotValueBufferSize"/> stackallocs;
    /// a longer marker would be silently truncated on read.
    /// </remarks>
    internal const byte OverlayTombstone = 0xff;

    private const int TombstoneLength = BaseFlatPersistence.RlpSlotValueBufferSize;

    private static readonly byte[] s_tombstone = CreateTombstone();

    private static byte[] CreateTombstone()
    {
        byte[] tombstone = new byte[TombstoneLength];
        tombstone[0] = OverlayTombstone;
        return tombstone;
    }

    internal static ReadOnlySpan<byte> TombstoneValue => s_tombstone;

    internal static bool IsTombstone(ReadOnlySpan<byte> value) =>
        value.Length == TombstoneLength && value[0] == OverlayTombstone;

    private const string TableFileExtension = ".st";
    private const byte EntityAccount = 0;
    private const byte EntityStorage = 1;
    private const int AccountKeyLength = BaseFlatPersistence.AccountKeyLength;
    private const int StorageKeyLength = BaseFlatPersistence.StorageKeyLength;

    // Registry entry in the Metadata column, per shard: key = "fbt1" + entity(1) + shard(u16 BE),
    // value = generation(i64 LE) + table length(i64 LE). Key length differs from the 32-byte hashed
    // metadata keys, so the two key families cannot collide.
    private static ReadOnlySpan<byte> RegistryKeyPrefix => "fbt1"u8;
    private const int RegistryKeyLength = 7;
    private const int RegistryValueLength = 16;

    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly string _directory;
    private readonly long _foldThresholdBytes;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly BaseTableView.ShardTable?[] _accountShards;
    private readonly BaseTableView.ShardTable?[] _storageShards;
    private BaseTableView _currentView;
    private long _nextGeneration;
    private long _overlayBytesSinceFold;
    private bool _disposed;

    internal BaseTableStore(
        IColumnsDb<FlatDbColumns> db,
        string directory,
        long foldThresholdBytes,
        ILogManager logManager,
        int accountShardCount = DefaultAccountShardCount,
        int storageShardCount = DefaultStorageShardCount)
    {
        ValidateShardCount(accountShardCount, nameof(accountShardCount));
        ValidateShardCount(storageShardCount, nameof(storageShardCount));
        _db = db;
        _directory = directory;
        _foldThresholdBytes = foldThresholdBytes;
        _logger = logManager.GetClassLogger<BaseTableStore>();
        _accountShards = new BaseTableView.ShardTable?[accountShardCount];
        _storageShards = new BaseTableView.ShardTable?[storageShardCount];
        Directory.CreateDirectory(directory);
        try
        {
            LoadRegistryAndSweepOrphans();
        }
        catch
        {
            // Close whatever mmaps were already opened; the tables' on-disk files are preserved (the
            // failure is configuration/corruption to be resolved by the operator, not a wipe).
            foreach (BaseTableView.ShardTable? shard in _accountShards) shard?.File.PersistOnShutdown();
            foreach (BaseTableView.ShardTable? shard in _storageShards) shard?.File.PersistOnShutdown();
            foreach (BaseTableView.ShardTable? shard in _accountShards) shard?.File.Dispose();
            foreach (BaseTableView.ShardTable? shard in _storageShards) shard?.File.Dispose();
            throw;
        }

        _currentView = new BaseTableView(_accountShards, _storageShards);

        static void ValidateShardCount(int count, string name)
        {
            if (count is < 1 or > 65536 || !BitOperations.IsPow2(count))
                throw new ArgumentOutOfRangeException(name, count, "Shard count must be a power of two in [1, 65536].");
        }
    }

    /// <summary>Lease the current immutable view of the shard tables. The caller must dispose the lease.</summary>
    internal BaseTableView LeaseView()
    {
        while (true)
        {
            BaseTableView view = Volatile.Read(ref _currentView);
            // A concurrent fold may dispose the view between the read and the acquire; retry on the new one.
            if (view.TryAcquireLease()) return view;
            // The other way the acquire keeps failing is a disposed store — fail instead of spinning.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        }
    }

    /// <summary>
    /// Commit a write batch under the store lock (serializing it against <see cref="Fold"/>'s
    /// snapshot→delete window, which must not interleave with a commit), account the overlay growth, and
    /// fold synchronously when the threshold is exceeded.
    /// </summary>
    internal void CommitBatch(long approxOverlayBytes, Action commit)
    {
        using Lock.Scope scope = _lock.EnterScope();
        commit();
        if (_disposed) return;
        _overlayBytesSinceFold += approxOverlayBytes;
        if (_foldThresholdBytes > 0 && _overlayBytesSinceFold >= _foldThresholdBytes)
            FoldLocked();
    }

    /// <summary>Synchronously fold the whole overlay into the shard tables. Exposed for tests and manual
    /// triggering; the automatic path is the threshold check in <see cref="CommitBatch"/>.</summary>
    internal void Fold()
    {
        using Lock.Scope scope = _lock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);
        FoldLocked();
    }

    private void FoldLocked()
    {
        // Counted at entry (not per shard changed) so a benchmark can assert that no fold work at all
        // overlapped its measurement window.
        Metrics.BaseStoreFolds++;
        List<ShardChange> changes = [];
        IColumnDbSnapshot<FlatDbColumns> snapshot = _db.CreateSnapshot();
        try
        {
            IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
            bool committed = false;
            try
            {
                IWriteBatch metadataBatch = batch.GetColumnBatch(FlatDbColumns.Metadata);
                FoldEntity(EntityAccount, _accountShards,
                    (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account),
                    batch.GetColumnBatch(FlatDbColumns.Account), metadataBatch, changes);
                FoldEntity(EntityStorage, _storageShards,
                    (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Storage),
                    batch.GetColumnBatch(FlatDbColumns.Storage), metadataBatch, changes);

                // All new shard files are fsynced by now; this single atomic commit repoints the registry
                // and drops the folded overlay keys together.
                batch.Dispose();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    batch.Clear();
                    batch.Dispose();
                    // Unregistered new files: dropping their only lease deletes them from disk.
                    foreach (ShardChange change in changes) change.NewTable?.File.Dispose();
                }
            }
        }
        finally
        {
            snapshot.Dispose();
        }

        if (changes.Count == 0) return;
        _db.Flush(onlyWal: true);

        foreach (ShardChange change in changes)
            (change.Entity == EntityAccount ? _accountShards : _storageShards)[change.Shard] = change.NewTable;

        BaseTableView oldView = _currentView;
        Volatile.Write(ref _currentView, new BaseTableView(_accountShards, _storageShards));
        oldView.Dispose();
        // Drop the store's own lease on the replaced files: each is deleted once its last reader drains.
        foreach (ShardChange change in changes) change.OldTable?.File.Dispose();

        _overlayBytesSinceFold = 0;
        if (_logger.IsDebug) _logger.Debug($"Folded flat base overlay into {changes.Count} shard tables");
    }

    private sealed record ShardChange(byte Entity, int Shard, BaseTableView.ShardTable? OldTable, BaseTableView.ShardTable? NewTable);

    private void FoldEntity(
        byte entity,
        BaseTableView.ShardTable?[] shards,
        ISortedKeyValueStore overlay,
        IWriteBatch overlayBatch,
        IWriteBatch metadataBatch,
        List<ShardChange> changes)
    {
        int keyLength = entity == EntityAccount ? AccountKeyLength : StorageKeyLength;
        for (int shard = 0; shard < shards.Length; shard++)
        {
            byte[] low = ShardLowKey(shard, shards.Length, keyLength);
            byte[] highExclusive = ShardHighKeyExclusive(shard, shards.Length, keyLength);

            // Shards the overlay never touched keep their table as-is.
            using (ISortedView probe = overlay.GetViewBetween(low, highExclusive))
            {
                if (!probe.MoveNext()) continue;
            }

            BaseTableView.ShardTable? old = shards[shard];
            using MergedOverlayBaseView merged = new(
                overlay.GetViewBetween(low, highExclusive),
                new BaseShardCursor(shards, low, highExclusive));
            BaseTableView.ShardTable? newTable = merged.MoveNext()
                ? WriteShardTable(entity, shard, merged)
                : null; // The overlay only tombstoned keys the shard held — the shard is now empty.

            // Every folded overlay key of the shard is deleted in the same batch that repoints the
            // registry — values are now in the new table, tombstones have shadowed their base records.
            using (ISortedView folded = overlay.GetViewBetween(low, highExclusive))
            {
                while (folded.MoveNext()) overlayBatch.Remove(folded.CurrentKey);
            }

            WriteRegistryEntry(metadataBatch, entity, shard, newTable);
            changes.Add(new ShardChange(entity, shard, old, newTable));
        }
    }

    /// <summary>
    /// Stream <paramref name="content"/> (already positioned on its first record) into a new shard table
    /// file and fsync it. The registry entry is the caller's responsibility — a crash before it commits
    /// leaves only an orphan file, swept at the next startup.
    /// </summary>
    private BaseTableView.ShardTable WriteShardTable(byte entity, int shard, ISortedView content)
    {
        long generation = _nextGeneration++;
        string path = Path.Combine(_directory, TableFileName(entity, shard, generation));
        long length;
        FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1);
        ArenaBufferWriter writer = new(stream, firstOffset: 0);
        try
        {
            SortedTableBuilder<ArenaBufferWriter> builder = new(ref writer);
            try
            {
                do
                {
                    builder.Add(content.CurrentKey, content.CurrentValue);
                } while (content.MoveNext());

                builder.Build();
            }
            finally
            {
                builder.Dispose();
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
            length = writer.Written;
        }
        finally
        {
            writer.Dispose();
        }

        return new BaseTableView.ShardTable(new ArenaFile(shard, path, length), length, generation);
    }

    /// <summary>
    /// Initial population: build every shard table from pre-sorted hashed-layout key/value streams (the
    /// fold path against an empty base). Both streams must be strictly ascending; the store must be empty.
    /// This is also the future migration/import step for an existing RocksDB base.
    /// </summary>
    internal void BulkLoad(
        IEnumerable<KeyValuePair<byte[], byte[]>> accounts,
        IEnumerable<KeyValuePair<byte[], byte[]>> storage)
    {
        using Lock.Scope scope = _lock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Array.Exists(_accountShards, static s => s is not null) || Array.Exists(_storageShards, static s => s is not null))
            throw new InvalidOperationException("Bulk load requires an empty base table store.");

        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        bool committed = false;
        try
        {
            IWriteBatch metadataBatch = batch.GetColumnBatch(FlatDbColumns.Metadata);
            BulkLoadEntity(EntityAccount, _accountShards, accounts, metadataBatch);
            BulkLoadEntity(EntityStorage, _storageShards, storage, metadataBatch);
            batch.Dispose();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                batch.Clear();
                batch.Dispose();
                // Roll back to the empty store: drop (and thereby delete) whatever tables were built.
                DropAll(_accountShards);
                DropAll(_storageShards);

                static void DropAll(BaseTableView.ShardTable?[] shards)
                {
                    foreach (BaseTableView.ShardTable? shard in shards) shard?.File.Dispose();
                    Array.Clear(shards);
                }
            }
        }

        _db.Flush(onlyWal: true);

        BaseTableView oldView = _currentView;
        Volatile.Write(ref _currentView, new BaseTableView(_accountShards, _storageShards));
        oldView.Dispose();
    }

    private void BulkLoadEntity(
        byte entity,
        BaseTableView.ShardTable?[] shards,
        IEnumerable<KeyValuePair<byte[], byte[]>> entries,
        IWriteBatch metadataBatch)
    {
        using IEnumerator<KeyValuePair<byte[], byte[]>> enumerator = entries.GetEnumerator();
        if (!enumerator.MoveNext()) return;

        while (true)
        {
            int shard = BaseTableView.ShardOf(enumerator.Current.Key, shards.Length);
            if (shards[shard] is not null)
                throw new InvalidOperationException("Bulk load input is not sorted: a shard's key range was visited twice.");

            ShardSliceCursor slice = new(enumerator, shard, shards.Length);
            BaseTableView.ShardTable table = WriteShardTable(entity, shard, PrimedCursor(slice));
            shards[shard] = table;
            WriteRegistryEntry(metadataBatch, entity, shard, table);
            if (slice.SourceExhausted) return;
        }

        // WriteShardTable expects a cursor already positioned on its first record.
        static ISortedView PrimedCursor(ShardSliceCursor slice)
        {
            slice.MoveNext();
            return slice;
        }
    }

    /// <summary>Adapts a primed enumerator into a per-shard cursor slice: yields records while they stay
    /// in <c>shard</c>, leaving the first out-of-shard record current on the enumerator for the next slice.</summary>
    private sealed class ShardSliceCursor(IEnumerator<KeyValuePair<byte[], byte[]>> source, int shard, int shardCount) : ISortedView
    {
        private bool _first = true;
        private bool _done;

        internal bool SourceExhausted { get; private set; }

        public bool MoveNext()
        {
            if (_first)
            {
                // The caller primed the source and routed to this shard by its current record.
                _first = false;
                return true;
            }

            if (_done) return false;
            if (!source.MoveNext())
            {
                SourceExhausted = true;
                _done = true;
                return false;
            }

            if (BaseTableView.ShardOf(source.Current.Key, shardCount) != shard)
            {
                _done = true;
                return false;
            }

            return true;
        }

        public bool StartBefore(ReadOnlySpan<byte> value) => throw new NotSupportedException();
        public ReadOnlySpan<byte> CurrentKey => source.Current.Key;
        public ReadOnlySpan<byte> CurrentValue => source.Current.Value;
        public void Dispose() { }
    }

    /// <summary>Drop every shard table and registry entry (snap-sync restart wipe). The registry is
    /// removed first, so a crash mid-clear leaves only orphan files for the startup sweep — never a
    /// registry pointing at deleted files.</summary>
    internal void Clear()
    {
        using Lock.Scope scope = _lock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);

        using (IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch())
        {
            IWriteBatch metadataBatch = batch.GetColumnBatch(FlatDbColumns.Metadata);
            RemoveRegistryEntries(EntityAccount, _accountShards, metadataBatch);
            RemoveRegistryEntries(EntityStorage, _storageShards, metadataBatch);
        }

        _db.Flush(onlyWal: true);

        BaseTableView oldView = _currentView;
        BaseTableView.ShardTable?[] oldAccounts = (BaseTableView.ShardTable?[])_accountShards.Clone();
        BaseTableView.ShardTable?[] oldStorage = (BaseTableView.ShardTable?[])_storageShards.Clone();
        Array.Clear(_accountShards);
        Array.Clear(_storageShards);
        Volatile.Write(ref _currentView, new BaseTableView(_accountShards, _storageShards));
        oldView.Dispose();
        foreach (BaseTableView.ShardTable? shard in oldAccounts) shard?.File.Dispose();
        foreach (BaseTableView.ShardTable? shard in oldStorage) shard?.File.Dispose();
        _overlayBytesSinceFold = 0;

        static void RemoveRegistryEntries(byte entity, BaseTableView.ShardTable?[] shards, IWriteBatch metadataBatch)
        {
            Span<byte> key = stackalloc byte[RegistryKeyLength];
            for (int shard = 0; shard < shards.Length; shard++)
            {
                if (shards[shard] is null) continue;
                EncodeRegistryKey(key, entity, shard);
                metadataBatch.Remove(key);
            }
        }
    }

    /// <summary>
    /// Advise the OS to drop the page-cache/mmap-resident pages of every live shard table
    /// (<c>madvise(MADV_DONTNEED)</c> + <c>posix_fadvise(POSIX_FADV_DONTNEED)</c>; no-ops outside Linux).
    /// Used after a Rocks→Arena conversion so its sequential writes don't leave the arena read path
    /// unfairly warm compared to a cold-booted RocksDB baseline.
    /// </summary>
    internal void EvictPageCache()
    {
        using Lock.Scope scope = _lock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EvictAll(_accountShards);
        EvictAll(_storageShards);

        static void EvictAll(BaseTableView.ShardTable?[] shards)
        {
            foreach (BaseTableView.ShardTable? shard in shards)
            {
                if (shard is null) continue;
                shard.File.AdviseDontNeed(0, shard.File.MappedSize);
                shard.File.FadviseDontNeed(0, shard.File.MappedSize);
            }
        }
    }

    public void Dispose()
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        _disposed = true;

        // Live tables must survive to the next session even if reader leases outlive the store.
        foreach (BaseTableView.ShardTable? shard in _accountShards) shard?.File.PersistOnShutdown();
        foreach (BaseTableView.ShardTable? shard in _storageShards) shard?.File.PersistOnShutdown();
        _currentView.Dispose();
        foreach (BaseTableView.ShardTable? shard in _accountShards) shard?.File.Dispose();
        foreach (BaseTableView.ShardTable? shard in _storageShards) shard?.File.Dispose();
    }

    private void LoadRegistryAndSweepOrphans()
    {
        IDb metadata = _db.GetColumnDb(FlatDbColumns.Metadata);
        HashSet<string> registeredFiles = new(StringComparer.Ordinal);
        foreach (KeyValuePair<byte[], byte[]?> kv in metadata.GetAll(ordered: false))
        {
            if (kv.Key.Length != RegistryKeyLength || !kv.Key.AsSpan(0, RegistryKeyPrefix.Length).SequenceEqual(RegistryKeyPrefix))
                continue;
            if (kv.Value is null || kv.Value.Length != RegistryValueLength)
                throw Corrupt($"registry entry has a {kv.Value?.Length ?? 0}-byte value");

            byte entity = kv.Key[RegistryKeyPrefix.Length];
            int shard = BinaryPrimitives.ReadUInt16BigEndian(kv.Key.AsSpan(RegistryKeyPrefix.Length + 1));
            long generation = BinaryPrimitives.ReadInt64LittleEndian(kv.Value);
            long length = BinaryPrimitives.ReadInt64LittleEndian(kv.Value.AsSpan(sizeof(long)));
            BaseTableView.ShardTable?[] shards = entity switch
            {
                EntityAccount => _accountShards,
                EntityStorage => _storageShards,
                _ => throw Corrupt($"registry entry has unknown entity byte {entity}"),
            };
            if ((uint)shard >= (uint)shards.Length)
                throw Corrupt($"registry entry addresses shard {shard} outside the configured {shards.Length}-shard layout");
            if (length <= 0)
                throw Corrupt($"registry entry declares a non-positive table length {length}");

            string fileName = TableFileName(entity, shard, generation);
            string path = Path.Combine(_directory, fileName);
            if (!File.Exists(path) || new FileInfo(path).Length < length)
                throw Corrupt($"registry references table file '{fileName}' that is missing or shorter than its recorded {length} bytes");

            shards[shard] = new BaseTableView.ShardTable(new ArenaFile(shard, path, length), length, generation);
            registeredFiles.Add(fileName);
            _nextGeneration = Math.Max(_nextGeneration, generation + 1);
        }

        // Sweep files the registry does not reference — tables a crashed fold fsynced but never
        // committed, or replaced tables whose deferred delete a crash skipped.
        foreach (string path in Directory.GetFiles(_directory, $"*{TableFileExtension}"))
        {
            string fileName = Path.GetFileName(path);
            if (registeredFiles.Contains(fileName)) continue;
            try
            {
                File.Delete(path);
                if (_logger.IsDebug) _logger.Debug($"Deleted orphan flat base table file {fileName}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (_logger.IsWarn) _logger.Warn($"Could not delete orphan flat base table file {fileName}: {e.Message}");
            }
        }

        static InvalidConfigurationException Corrupt(string detail) => new(
            $"Corrupt flat base shard registry: {detail}. Wipe the flat DB and re-sync.", -1);
    }

    private static string TableFileName(byte entity, int shard, long generation) =>
        $"{(entity == EntityAccount ? 'a' : 's')}{shard:x4}_{generation:x8}{TableFileExtension}";

    private static void EncodeRegistryKey(Span<byte> key, byte entity, int shard)
    {
        RegistryKeyPrefix.CopyTo(key);
        key[RegistryKeyPrefix.Length] = entity;
        BinaryPrimitives.WriteUInt16BigEndian(key[(RegistryKeyPrefix.Length + 1)..], (ushort)shard);
    }

    private static void WriteRegistryEntry(IWriteBatch metadataBatch, byte entity, int shard, BaseTableView.ShardTable? table)
    {
        Span<byte> key = stackalloc byte[RegistryKeyLength];
        EncodeRegistryKey(key, entity, shard);
        if (table is null)
        {
            metadataBatch.Remove(key);
            return;
        }

        Span<byte> value = stackalloc byte[RegistryValueLength];
        BinaryPrimitives.WriteInt64LittleEndian(value, table.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(value[sizeof(long)..], table.Length);
        metadataBatch.PutSpan(key, value);
    }

    /// <summary>Smallest key of <paramref name="shard"/>: the shard's 16-bit prefix over zeros.</summary>
    private static byte[] ShardLowKey(int shard, int shardCount, int keyLength)
    {
        byte[] key = new byte[keyLength];
        BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)(shard << (16 - BitOperations.Log2((uint)shardCount))));
        return key;
    }

    /// <summary>Exclusive upper bound of <paramref name="shard"/>: the next shard's smallest key, or for
    /// the last shard a <c>keyLength + 1</c>-byte 0xff key that no fixed-length key can reach.</summary>
    private static byte[] ShardHighKeyExclusive(int shard, int shardCount, int keyLength)
    {
        if (shard == shardCount - 1)
        {
            byte[] max = new byte[keyLength + 1];
            max.AsSpan().Fill(0xff);
            return max;
        }

        return ShardLowKey(shard + 1, shardCount, keyLength);
    }
}
