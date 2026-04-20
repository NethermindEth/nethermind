// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// A hybrid layout that syncs and snap-serves like <see cref="RocksDbPersistence"/> (hashed keys in Storage) but
/// routes new block-processing storage writes to a dedicated <see cref="FlatDbColumns.PreimageStorage"/> column
/// using raw address and slot bytes as keys instead of keccak hashes.
///
/// Benefits of PreimageStorage:
/// - Skips keccak hashing on every storage write (~300 ns).
/// - Better compression ratio and block-cache locality for recently-written slots.
///
/// Trade-offs:
/// - Read path: try PreimageStorage first, fall back to hashed Storage. Null lookups (~40% of reads) pay one
///   extra bloom-filter check on PreimageStorage before falling through.
/// - On SetStorage, probes hashed Storage and emits a Remove if the slot was previously stored there, so the
///   fallback never returns stale data.
/// - Raw iterator and snap-serve paths use hashed Storage only (trie columns are unaffected).
/// </summary>
public class HybridPreimagePersistence(IColumnsDb<FlatDbColumns> db) : IPersistence
{
    private readonly WriteBufferAdjuster _adjuster = new(db);

    public void Flush() => db.Flush();

    public void Clear() => BasePersistence.ClearAllColumns(db);

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        IColumnDbSnapshot<FlatDbColumns> snapshot = db.CreateSnapshot();
        try
        {
            BaseTriePersistence.Reader trieReader = new(
                snapshot.GetColumn(FlatDbColumns.StateTopNodes),
                snapshot.GetColumn(FlatDbColumns.StateNodes),
                snapshot.GetColumn(FlatDbColumns.StorageNodes),
                snapshot.GetColumn(FlatDbColumns.FallbackNodes)
            );

            StateId currentState = BasePersistence.ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

            return new BasePersistence.Reader<HybridFlatReader, BaseTriePersistence.Reader>(
                new HybridFlatReader(
                    new PreimageRocksdbPersistence.FakeHashFlatReader<BaseFlatPersistence.Reader>(
                        new BaseFlatPersistence.Reader(
                            (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account),
                            (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.PreimageStorage),
                            isPreimageMode: true
                        )
                    ),
                    new BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>(
                        new BaseFlatPersistence.Reader(
                            (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account),
                            (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Storage),
                            isPreimageMode: false
                        )
                    )
                ),
                trieReader,
                currentState,
                new Reactive.AnonymousDisposable(snapshot.Dispose)
            );
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        IColumnDbSnapshot<FlatDbColumns> dbSnap = db.CreateSnapshot();
        StateId currentState = BasePersistence.ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (from != StateId.Sync && to != StateId.Sync && currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();

        IWriteBatch accountBatch = _adjuster.Wrap(batch, FlatDbColumns.Account, flags);
        IWriteBatch storageBatch = _adjuster.Wrap(batch, FlatDbColumns.Storage, flags);
        IWriteBatch preimageStorageBatch = _adjuster.Wrap(batch, FlatDbColumns.PreimageStorage, flags);
        IWriteBatch stateTopNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateTopNodes, flags);
        IWriteBatch stateNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateNodes, flags);
        IWriteBatch storageNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StorageNodes, flags);
        IWriteBatch fallbackNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.FallbackNodes, flags);

        ISortedKeyValueStore accountSnap = (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Account);
        ISortedKeyValueStore storageSnap = (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage);
        ISortedKeyValueStore preimageStorageSnap = (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.PreimageStorage);

        BaseTriePersistence.WriteBatch trieWriteBatch = new(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            stateTopNodesBatch,
            stateNodesBatch,
            storageNodesBatch,
            fallbackNodesBatch,
            flags
        );

        StateId fromCopy = from;
        StateId toCopy = to;
        return new BasePersistence.WriteBatch<HybridFlatWriteBatch, BaseTriePersistence.WriteBatch>(
            new HybridFlatWriteBatch(
                new BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>(
                    new BaseFlatPersistence.WriteBatch(accountSnap, storageSnap, accountBatch, storageBatch, flags)
                ),
                new BaseFlatPersistence.WriteBatch(accountSnap, preimageStorageSnap, accountBatch, preimageStorageBatch, flags),
                storageSnap
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                if (fromCopy != StateId.Sync && toCopy != StateId.Sync)
                    BasePersistence.SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), toCopy);
                batch.Dispose();
                dbSnap.Dispose();
                _adjuster.OnBatchDisposed();
                if (!flags.HasFlag(WriteFlags.DisableWAL))
                {
                    db.Flush(onlyWal: true);
                }
            })
        );
    }

    private struct HybridFlatReader(
        PreimageRocksdbPersistence.FakeHashFlatReader<BaseFlatPersistence.Reader> preimageReader,
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> hashedReader
    ) : BasePersistence.IFlatReader
    {
        private PreimageRocksdbPersistence.FakeHashFlatReader<BaseFlatPersistence.Reader> _preimageReader = preimageReader;
        private BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> _hashedReader = hashedReader;

        public Account? GetAccount(Address address) => _hashedReader.GetAccount(address);

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            if (_preimageReader.TryGetSlot(address, slot, ref outValue)) return true;
            return _hashedReader.TryGetSlot(address, slot, ref outValue);
        }

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => _hashedReader.GetAccountRaw(addrHash);

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue) =>
            _hashedReader.TryGetSlotRaw(address, slotHash, ref outValue);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            _hashedReader.CreateAccountIterator(startKey, endKey);

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            _hashedReader.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);

        public bool IsPreimageMode => false;
    }

    private struct HybridFlatWriteBatch(
        BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch> hashedBatch,
        BaseFlatPersistence.WriteBatch preimageStorageBatch,
        ISortedKeyValueStore storageSnap
    ) : BasePersistence.IFlatWriteBatch
    {
        private BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch> _hashedBatch = hashedBatch;
        private BaseFlatPersistence.WriteBatch _preimageStorageBatch = preimageStorageBatch;
        private readonly ISortedKeyValueStore _storageSnap = storageSnap;

        public void SelfDestruct(Address addr)
        {
            _hashedBatch.SelfDestruct(addr);

            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);
            _preimageStorageBatch.SelfDestruct(fakeAddrHash);
        }

        public void SetAccount(Address addr, Account? account) => _hashedBatch.SetAccount(addr, account);

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            // Write to PreimageStorage using raw address + slot bytes as key.
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);
            ValueHash256 fakeSlotHash = ValueKeccak.Zero;
            slot.ToBigEndian(fakeSlotHash.BytesAsSpan);
            _preimageStorageBatch.SetStorage(fakeAddrHash, fakeSlotHash, value);

            // If a pre-hybrid entry exists in hashed Storage, remove it to keep the fallback read correct.
            ValueHash256 hashedSlot = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref hashedSlot);
            ReadOnlySpan<byte> hashedKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
                stackalloc byte[BaseFlatPersistence.StorageKeyLength], addr.ToAccountPath, hashedSlot);
            Span<byte> probeBuffer = stackalloc byte[40];
            if (_storageSnap.Get(hashedKey, probeBuffer) > 0)
                _hashedBatch.SetStorageRaw(addr.ToAccountPath, hashedSlot, null);
        }

        public void SetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? value) =>
            _hashedBatch.SetStorageRaw(addrHash, slotHash, value);

        public void SetAccountRaw(in ValueHash256 addrHash, Account account) =>
            _hashedBatch.SetAccountRaw(addrHash, account);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _hashedBatch.DeleteAccountRange(fromPath, toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            _hashedBatch.DeleteStorageRange(addressHash, fromPath, toPath);
    }
}
