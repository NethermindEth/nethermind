// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// <see cref="IPersistence"/> whose Account/Storage base tier lives in prefix-sharded immutable sorted
/// tables (mmap arena files, see <see cref="BaseTableStore"/>), with the RocksDB Account/Storage columns
/// acting as a small delta overlay. Selected by <see cref="IFlatDbConfig.BaseStore"/> =
/// <see cref="FlatBaseStore.Arena"/>; trie nodes and metadata stay in RocksDB exactly as in
/// <see cref="RocksDbPersistence"/>.
/// </summary>
/// <remarks>
/// Reads are overlay-first, then the shard table; absent from both means nonexistent. Deletions are
/// overlay tombstones (<see cref="BaseTableStore.OverlayTombstone"/>) so they shadow base records until a
/// fold drops both. Write batches keep the RocksDB crash semantics unchanged — StateId, trie nodes and the
/// flat delta commit in one RocksDB batch; only the fold moves rows out of RocksDB, atomically per batch.
/// </remarks>
public class ArenaBasePersistence : IPersistence, IDisposable
{
    private static readonly byte[] BaseStoreKindKey = Keccak.Compute("BaseStoreKind").BytesToArray();

    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly BaseTableStore _store;
    private readonly WriteBufferAdjuster _adjuster;
    private readonly bool _rlpWrapSlots;
    private int _layoutPersisted;
    private int _kindPersisted;

    /// <param name="directory">Directory holding the shard table arena files, conventionally
    /// <c>{BaseDbPath}/flatBase</c> (composed by the DI module).</param>
    public ArenaBasePersistence(IColumnsDb<FlatDbColumns> db, string directory, IFlatDbConfig config, ILogManager logManager)
        : this(db, directory, config, logManager, BaseTableStore.DefaultAccountShardCount, BaseTableStore.DefaultStorageShardCount)
    {
    }

    /// <summary>Test entry point: shard counts stay internal constants in the prototype.</summary>
    internal ArenaBasePersistence(
        IColumnsDb<FlatDbColumns> db,
        string directory,
        IFlatDbConfig config,
        ILogManager logManager,
        int accountShardCount,
        int storageShardCount)
    {
        _db = db;
        _layoutPersisted = BasePersistence.ValidateLayoutReturnFlag(db, FlatLayout.Flat);
        ValidateBaseStoreKind(db, FlatBaseStore.Arena, config.ConvertBaseStore);
        // Both tiers store slot values verbatim in the DB's own encoding, so the arena inherits it rather
        // than imposing one; the deletion marker is unambiguous under either (see BaseTableStore.TombstoneValue).
        _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<ArenaBasePersistence>());
        _adjuster = new WriteBufferAdjuster(db, config.PersistenceWriteBufferFloor);
        _store = new BaseTableStore(db, directory, config.BaseFoldThresholdBytes, logManager, accountShardCount, storageShardCount, config.BaseStoreBloomBitsPerKey);
    }

    /// <summary>
    /// Validates that <paramref name="configured"/> matches the base store that wrote this flat DB, so a
    /// restart with the wrong backend fails loudly instead of reading half a state. The Arena store stamps
    /// a kind marker into the Metadata column on its first batch; a marker-less DB that already carries
    /// state was necessarily written by the (marker-less) <see cref="FlatBaseStore.Rocks"/> store.
    /// </summary>
    /// <param name="allowConversion">When <see cref="IFlatDbConfig.ConvertBaseStore"/> is set, a
    /// Rocks-owned DB is accepted for an Arena configuration — the startup conversion
    /// (<see cref="FlatBaseStoreConverter"/>) will migrate it. Never relaxes the reverse direction.</param>
    /// <exception cref="InvalidConfigurationException">The DB belongs to the other base store.</exception>
    public static void ValidateBaseStoreKind(IColumnsDb<FlatDbColumns> db, FlatBaseStore configured, bool allowConversion = false)
    {
        bool rocksAcceptedAsArena = allowConversion && configured == FlatBaseStore.Arena;
        FlatBaseStore? stored = ReadBaseStoreKind(db);
        if (stored is { } kind)
        {
            if (kind != configured && !(kind == FlatBaseStore.Rocks && rocksAcceptedAsArena))
                throw new InvalidConfigurationException(
                    $"Flat DB was previously written with base store '{kind}', but 'FlatDb.BaseStore' is '{configured}'. " +
                    $"Either set it back to '{kind}', or wipe the flat DB and re-sync.", -1);
            return;
        }

        if (configured != FlatBaseStore.Rocks && !rocksAcceptedAsArena && BasePersistence.HasCurrentState(db.GetColumnDb(FlatDbColumns.Metadata)))
            throw new InvalidConfigurationException(
                $"Flat DB holds state written by the '{FlatBaseStore.Rocks}' base store, but 'FlatDb.BaseStore' is '{configured}'. " +
                $"Either set it back to '{FlatBaseStore.Rocks}', enable 'FlatDb.ConvertBaseStore', or wipe the flat DB and re-sync.", -1);
    }

    /// <summary>The base store recorded in the Metadata column, or <c>null</c> when no marker was ever
    /// stamped (a fresh DB, or one only the marker-less Rocks store has written).</summary>
    internal static FlatBaseStore? ReadBaseStoreKind(IColumnsDb<FlatDbColumns> db)
    {
        byte[]? stored = db.GetColumnDb(FlatDbColumns.Metadata).Get(BaseStoreKindKey);
        if (stored is not { Length: > 0 }) return null;
        FlatBaseStore kind = (FlatBaseStore)stored[0];
        if (!Enum.IsDefined(kind))
            throw new InvalidConfigurationException(
                $"Flat DB metadata contains an unrecognized base store kind byte '{stored[0]}'. The DB may be corrupt or was written by a newer version.", -1);
        return kind;
    }

    public void Flush() => _db.Flush();

    public void Clear()
    {
        // Registry entries and shard files first (crash-safe on its own), then the standard column wipe.
        _store.Clear();
        BasePersistence.ClearAllColumns(_db);
    }

    public void Dispose() => _store.Dispose();

    /// <summary>Synchronously folds the overlay into the shard tables. Test/diagnostic entry point; the
    /// automatic trigger is <see cref="IFlatDbConfig.BaseFoldThresholdBytes"/>.</summary>
    internal void Fold() => _store.Fold();

    /// <inheritdoc cref="BaseTableStore.BulkLoad"/>
    internal void BulkLoad(
        IEnumerable<KeyValuePair<byte[], byte[]>> accounts,
        IEnumerable<KeyValuePair<byte[], byte[]>> storage)
    {
        _store.BulkLoad(accounts, storage);
        // The layout marker may be stamped here (a bulk-loaded DB may never see a write batch), but the
        // base-store kind marker is not: writing it is the caller's commit point — see
        // FlatBaseStoreConverter for why its position is load-bearing for crash safety.
        BasePersistence.RecordLayoutOnFirstBatch(_db.GetColumnDb(FlatDbColumns.Metadata), ref _layoutPersisted, FlatLayout.Flat);
    }

    /// <summary>Stamp the Arena base-store kind marker and make it durable. This is the Rocks→Arena
    /// conversion's commit point: before it, a restart re-converts from the intact overlay; after it, the
    /// DB boots as Arena and any not-yet-cleaned overlay rows merely shadow identical base values.</summary>
    internal void WriteBaseStoreKindMarker()
    {
        _db.GetColumnDb(FlatDbColumns.Metadata).PutSpan(BaseStoreKindKey, [(byte)FlatBaseStore.Arena]);
        Interlocked.Exchange(ref _kindPersisted, 1);
        _db.Flush(onlyWal: true);
    }

    /// <inheritdoc cref="BaseTableStore.Clear"/>
    internal void ClearShardTables() => _store.Clear();

    /// <inheritdoc cref="BaseTableStore.EvictPageCache"/>
    internal void EvictShardTablePageCache() => _store.EvictPageCache();

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        IColumnDbSnapshot<FlatDbColumns> snapshot = _db.CreateSnapshot();
        BaseTableView? view = null;
        try
        {
            view = _store.LeaseView();
            BaseTriePersistence.Reader trieReader = new(
                snapshot.GetColumn(FlatDbColumns.StateTopNodes),
                snapshot.GetColumn(FlatDbColumns.StateNodes),
                snapshot.GetColumn(FlatDbColumns.StorageNodes),
                snapshot.GetColumn(FlatDbColumns.FallbackNodes)
            );

            StateId currentState = BasePersistence.ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

            BaseTableView viewCopy = view;
            return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<ArenaBaseFlatReader>, BaseTriePersistence.Reader>(
                new BasePersistence.ToHashedFlatReader<ArenaBaseFlatReader>(
                    new ArenaBaseFlatReader(
                        (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account),
                        (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Storage),
                        viewCopy,
                        _rlpWrapSlots
                    )
                ),
                trieReader,
                currentState,
                new Reactive.AnonymousDisposable(() =>
                {
                    viewCopy.Dispose();
                    snapshot.Dispose();
                })
            );
        }
        catch
        {
            view?.Dispose();
            snapshot.Dispose();
            throw;
        }
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
    {
        IColumnDbSnapshot<FlatDbColumns> dbSnap = _db.CreateSnapshot();
        StateId currentState = BasePersistence.ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (from != StateId.Sync && to != StateId.Sync && currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        BaseTableView view = _store.LeaseView();
        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();

        IWriteBatch accountBatch = _adjuster.Wrap(batch, FlatDbColumns.Account, flags);
        IWriteBatch storageBatch = _adjuster.Wrap(batch, FlatDbColumns.Storage, flags);
        IWriteBatch stateTopNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateTopNodes, flags);
        IWriteBatch stateNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateNodes, flags);
        IWriteBatch storageNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StorageNodes, flags);
        IWriteBatch fallbackNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.FallbackNodes, flags);

        BaseTriePersistence.WriteBatch trieWriteBatch = new(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            stateTopNodesBatch,
            stateNodesBatch,
            storageNodesBatch,
            fallbackNodesBatch,
            flags);

        OverlayWriteCounter counter = new();
        StateId fromCopy = from;
        StateId toCopy = to;

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<ArenaOverlayWriteBatch>, BaseTriePersistence.WriteBatch>(
            new BasePersistence.ToHashedWriteBatch<ArenaOverlayWriteBatch>(
                new ArenaOverlayWriteBatch(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Account),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                    accountBatch,
                    storageBatch,
                    view,
                    flags,
                    counter,
                    _rlpWrapSlots
                )
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                IWriteBatch metadataBatch = batch.GetColumnBatch(FlatDbColumns.Metadata);
                if (fromCopy != StateId.Sync && toCopy != StateId.Sync)
                    BasePersistence.SetCurrentState(metadataBatch, toCopy);
                BasePersistence.RecordLayoutOnFirstBatch(metadataBatch, ref _layoutPersisted, FlatLayout.Flat);
                RecordKindOnFirstBatch(metadataBatch);
                // The commit (and the fold it may trigger) runs under the store lock, so a fold's
                // overlay snapshot can never interleave with a batch commit.
                _store.CommitBatch(counter.Bytes, () =>
                {
                    batch.Dispose();
                    if (!flags.HasFlag(WriteFlags.DisableWAL))
                    {
                        _db.Flush(onlyWal: true);
                    }
                });
                view.Dispose();
                dbSnap.Dispose();
                _adjuster.OnBatchDisposed();
            })
        );
    }

    private void RecordKindOnFirstBatch(IWriteOnlyKeyValueStore metadataBatch)
    {
        if (Interlocked.CompareExchange(ref _kindPersisted, 1, 0) == 0)
            metadataBatch.PutSpan(BaseStoreKindKey, [(byte)FlatBaseStore.Arena]);
    }

    /// <summary>Approximate bytes a batch wrote into the overlay columns, driving the fold threshold.</summary>
    internal sealed class OverlayWriteCounter
    {
        internal long Bytes;

        internal void Add(long bytes) => Bytes += bytes;
    }

    /// <summary>Overlay-first flat reads: an overlay hit (including a tombstone, which reads as absent)
    /// never consults the base; an overlay miss falls through to the leased shard-table view.</summary>
    internal readonly struct ArenaBaseFlatReader(
        ISortedKeyValueStore accountOverlay,
        ISortedKeyValueStore storageOverlay,
        BaseTableView view,
        bool rlpWrapSlots
    ) : BasePersistence.IHashedFlatReader
    {
        public bool IsPreimageMode => false;

        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
        {
            ReadOnlySpan<byte> key = BaseFlatPersistence.EncodeAccountKeyHashed(stackalloc byte[BaseFlatPersistence.AccountKeyLength], address);
            int size = accountOverlay.Get(key, outBuffer);
            if (size != 0) return BaseTableStore.IsTombstone(outBuffer[..size]) ? 0 : size;
            return view.GetAccount(key, outBuffer);
        }

        [SkipLocalsInit]
        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref SlotValue outValue)
        {
            ReadOnlySpan<byte> key = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[BaseFlatPersistence.StorageKeyLength], address, slot);
            Span<byte> buffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
            int size = storageOverlay.Get(key, buffer);
            if (size != 0)
            {
                if (BaseTableStore.IsTombstone(buffer[..size])) return false;
            }
            else if (!view.TryGetStorage(key, buffer, out size))
            {
                return false;
            }

            BaseFlatPersistence.DecodeSlotValue(buffer[..size], rlpWrapSlots, ref outValue);
            return true;
        }

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey)
        {
            byte[] start = startKey.Bytes[..BaseFlatPersistence.AccountKeyLength].ToArray();
            byte[] end = endKey.Bytes[..BaseFlatPersistence.AccountKeyLength].ToArray();

            return new BaseFlatPersistence.AccountIterator(new MergedOverlayBaseView(
                accountOverlay.GetViewBetween(start, end),
                view.CreateAccountCursor(start, end)));
        }

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey)
        {
            byte[] firstKey = new byte[BaseFlatPersistence.StorageKeyLength];
            byte[] lastKey = new byte[BaseFlatPersistence.StorageKeyLength + 1];
            BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(firstKey, accountKey, startSlotKey);
            BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(lastKey.AsSpan(0, BaseFlatPersistence.StorageKeyLength), accountKey, endSlotKey);
            lastKey[BaseFlatPersistence.StorageKeyLength] = 0; // Exclusive upper bound

            return new BaseFlatPersistence.StorageIterator(
                new MergedOverlayBaseView(
                    storageOverlay.GetViewBetween(firstKey, lastKey),
                    view.CreateStorageCursor(firstKey, lastKey)),
                accountKey.Bytes[BasePersistence.StoragePrefixPortion..BaseFlatPersistence.AccountKeyLength].ToArray(),
                BasePersistence.StoragePrefixPortion,
                rlpWrapSlots);
        }
    }

    /// <summary>
    /// Overlay write batch: value writes delegate to <see cref="BaseFlatPersistence.WriteBatch"/>
    /// unchanged, while every deletion (account removal, slot clear, self-destruct, range delete) becomes
    /// an overlay tombstone so it keeps shadowing the base shard tables. Self-destructs and range deletes
    /// tombstone the union of the overlay-snapshot scan (as the RocksDB backend scans it) and the base
    /// shard range, so base-only slots die too.
    /// </summary>
    internal struct ArenaOverlayWriteBatch : BasePersistence.IHashedFlatWriteBatch
    {
        private readonly ISortedKeyValueStore _storageSnap;
        private readonly ISortedKeyValueStore _accountSnap;
        private readonly IWriteBatch _accountBatch;
        private readonly IWriteBatch _storageBatch;
        private readonly BaseTableView _view;
        private readonly WriteFlags _flags;
        private readonly OverlayWriteCounter _counter;
        private BaseFlatPersistence.WriteBatch _inner;

        internal ArenaOverlayWriteBatch(
            ISortedKeyValueStore accountSnap,
            ISortedKeyValueStore storageSnap,
            IWriteBatch accountBatch,
            IWriteBatch storageBatch,
            BaseTableView view,
            WriteFlags flags,
            OverlayWriteCounter counter,
            bool rlpWrapSlots)
        {
            _accountSnap = accountSnap;
            _storageSnap = storageSnap;
            _accountBatch = accountBatch;
            _storageBatch = storageBatch;
            _view = view;
            _flags = flags;
            _counter = counter;
            _inner = new BaseFlatPersistence.WriteBatch(accountSnap, storageSnap, accountBatch, storageBatch, flags, rlpWrapSlots);
        }

        public void SetAccount(in ValueHash256 addrHash, ReadOnlySpan<byte> value)
        {
            _counter.Add(BaseFlatPersistence.AccountKeyLength + value.Length);
            _inner.SetAccount(addrHash, value);
        }

        public void RemoveAccount(in ValueHash256 addrHash)
        {
            _counter.Add(BaseFlatPersistence.AccountKeyLength + 1);
            _accountBatch.PutSpan(addrHash.Bytes[..BaseFlatPersistence.AccountKeyLength], BaseTableStore.TombstoneValue, _flags);
        }

        public void SetStorage(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? slot)
        {
            _counter.Add(BaseFlatPersistence.StorageKeyLength + BaseFlatPersistence.RlpSlotValueBufferSize);
            if (slot.HasValue)
            {
                _inner.SetStorage(addrHash, slotHash, slot);
            }
            else
            {
                ReadOnlySpan<byte> key = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
                    stackalloc byte[BaseFlatPersistence.StorageKeyLength], addrHash, slotHash);
                _storageBatch.PutSpan(key, BaseTableStore.TombstoneValue, _flags);
            }
        }

        public void SetStorageEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            _counter.Add(BaseFlatPersistence.StorageKeyLength + rlpValue.Length);
            _inner.SetStorageEncoded(addrHash, slotHash, rlpValue);
        }

        [SkipLocalsInit]
        public void SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKey = stackalloc byte[BasePersistence.StoragePrefixPortion];
            Span<byte> lastKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength + 1];
            BasePersistence.CreateStorageRange(accountPath.Bytes, firstKey, lastKey, BasePersistence.StoragePrefixPortion);
            TombstoneStorageRange(firstKey, lastKey,
                accountPath.Bytes[BasePersistence.StoragePrefixPortion..BaseFlatPersistence.AccountKeyLength]);
        }

        [SkipLocalsInit]
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            Span<byte> firstKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
            Span<byte> lastKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength + 1]; // +1 for exclusive upper bound
            fromPath.Bytes[..BaseFlatPersistence.AccountKeyLength].CopyTo(firstKey);
            toPath.Bytes[..BaseFlatPersistence.AccountKeyLength].CopyTo(lastKey);
            lastKey[BaseFlatPersistence.AccountKeyLength] = 0;

            using (ISortedView overlayView = _accountSnap.GetViewBetween(firstKey, lastKey))
            {
                while (overlayView.MoveNext())
                {
                    if (overlayView.CurrentKey.Length != BaseFlatPersistence.AccountKeyLength) continue;
                    Tombstone(_accountBatch, overlayView.CurrentKey);
                }
            }

            using BaseShardCursor baseCursor = _view.CreateAccountCursor(firstKey.ToArray(), lastKey.ToArray());
            while (baseCursor.MoveNext())
                Tombstone(_accountBatch, baseCursor.CurrentKey);
        }

        [SkipLocalsInit]
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            Span<byte> firstKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
            Span<byte> lastKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength + 1];
            BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(firstKey, addressHash, fromPath);
            BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(lastKey[..BaseFlatPersistence.StorageKeyLength], addressHash, toPath);
            lastKey[BaseFlatPersistence.StorageKeyLength] = 0;
            TombstoneStorageRange(firstKey, lastKey,
                addressHash.Bytes[BasePersistence.StoragePrefixPortion..BaseFlatPersistence.AccountKeyLength]);
        }

        /// <summary>Tombstone every storage key in <c>[firstKey, lastKey)</c> whose trailing address bytes
        /// match <paramref name="expectedSuffix"/>, in both the overlay snapshot and the base shard range
        /// (mirroring <see cref="BasePersistence.DeleteMatchingKeys(ISortedKeyValueStore,IWriteBatch,ReadOnlySpan{byte},ReadOnlySpan{byte},int,ReadOnlySpan{byte})"/>'s suffix re-check).</summary>
        private void TombstoneStorageRange(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey, ReadOnlySpan<byte> expectedSuffix)
        {
            const int suffixOffset = BasePersistence.StoragePrefixPortion + 32; // prefix + slot hash

            using (ISortedView overlayView = _storageSnap.GetViewBetween(firstKey, lastKey))
            {
                while (overlayView.MoveNext())
                {
                    ReadOnlySpan<byte> key = overlayView.CurrentKey;
                    if (key.Length == BaseFlatPersistence.StorageKeyLength && key[suffixOffset..].SequenceEqual(expectedSuffix))
                        Tombstone(_storageBatch, key);
                }
            }

            using BaseShardCursor baseCursor = _view.CreateStorageCursor(firstKey.ToArray(), lastKey.ToArray());
            while (baseCursor.MoveNext())
            {
                ReadOnlySpan<byte> key = baseCursor.CurrentKey;
                if (key[suffixOffset..].SequenceEqual(expectedSuffix))
                    Tombstone(_storageBatch, key);
            }
        }

        private void Tombstone(IWriteBatch batch, ReadOnlySpan<byte> key)
        {
            _counter.Add(key.Length + 1);
            batch.PutSpan(key, BaseTableStore.TombstoneValue, _flags);
        }
    }
}
