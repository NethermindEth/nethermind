// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// <see cref="IPbtPersistence"/> over the pbt columns database. Readers wrap a column-family
/// snapshot; write batches are atomic across all columns and record the new
/// <see cref="StateId"/> in the metadata column of the same batch.
/// </summary>
public class PbtRocksDbPersistence(IColumnsDb<PbtColumns> db) : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;

    private const int StorageKeyLength = 64;

    /// <summary>Maps a stem to its leaf-blob column by zone (the tree layer is column-agnostic, so the routing lives here).</summary>
    private static PbtColumns LeafColumn(in Stem stem) => stem.Zone switch
    {
        0x0 => PbtColumns.AccountLeaves,
        0x1 => PbtColumns.CodeLeaves,
        >= 0x8 => PbtColumns.StorageLeaves,
        _ => throw new NotSupportedException($"Zone {stem.Zone} is reserved"),
    };

    public IPbtPersistence.IReader CreateReader() => new Reader(db.CreateSnapshot());

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to)
    {
        StateId currentState = ReadCurrentState(db.GetColumnDb(PbtColumns.Metadata));
        if (currentState != from)
        {
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {currentState}");
        }

        return new WriteBatch(db, to);
    }

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore metadata)
    {
        byte[]? value = metadata.Get(CurrentStateKey);
        return value is null
            ? StateId.PreGenesis
            : new StateId(BinaryPrimitives.ReadUInt64BigEndian(value), new ValueHash256(value.AsSpan(8, 32)));
    }

    private static void WriteCurrentState(IWriteOnlyKeyValueStore metadata, in StateId stateId)
    {
        Span<byte> value = stackalloc byte[40];
        BinaryPrimitives.WriteUInt64BigEndian(value, stateId.BlockNumber);
        stateId.StateRoot.Bytes.CopyTo(value[8..]);
        metadata.PutSpan(CurrentStateKey, value);
    }

    private static void EncodeStorageKey(in ValueHash256 addressHash, in UInt256 slot, Span<byte> key)
    {
        addressHash.Bytes.CopyTo(key);
        slot.ToBigEndian(key[32..]);
    }

    private sealed class Reader(IColumnDbSnapshot<PbtColumns> snapshot) : IPbtPersistence.IReader
    {
        public StateId CurrentState { get; } = ReadCurrentState(snapshot.GetColumn(PbtColumns.Metadata));

        public Account? GetAccount(Address address)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            byte[]? value = snapshot.GetColumn(PbtColumns.Account).Get(addressHash.Bytes);
            if (value is null) return null;

            RlpReader reader = new(value);
            return AccountDecoder.Slim.Decode(ref reader);
        }

        public EvmWord GetSlot(Address address, in UInt256 slot)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            Span<byte> key = stackalloc byte[StorageKeyLength];
            EncodeStorageKey(addressHash, slot, key);
            byte[]? stored = snapshot.GetColumn(PbtColumns.Storage).Get(key);
            return stored is null ? default : EvmWordSlot.FromStripped(stored);
        }

        public byte[]? GetLeafBlob(in Stem stem) => snapshot.GetColumn(LeafColumn(stem)).Get(stem.Bytes);

        public byte[]? GetTrieNode(in TrieNodeKey key)
        {
            Span<byte> dbKey = stackalloc byte[TrieNodeKey.Length];
            key.WriteTo(dbKey);
            return snapshot.GetColumn(PbtColumns.TrieNodes).Get(dbKey);
        }

        public void Dispose() => snapshot.Dispose();
    }

    private sealed class WriteBatch(IColumnsDb<PbtColumns> db, StateId to) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnDbSnapshot<PbtColumns> _preBatchSnapshot = db.CreateSnapshot();
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();

        public void SetAccount(Address address, Account? account)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            IWriteBatch accounts = _batch.GetColumnBatch(PbtColumns.Account);
            if (account is null)
            {
                accounts.Remove(addressHash.Bytes);
            }
            else
            {
                using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
                accounts.PutSpan(addressHash.Bytes, rlp);
            }
        }

        public void SetSlot(Address address, in UInt256 slot, in EvmWord value)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            Span<byte> key = stackalloc byte[StorageKeyLength];
            EncodeStorageKey(addressHash, slot, key);
            IWriteBatch storage = _batch.GetColumnBatch(PbtColumns.Storage);
            if (EvmWordSlot.IsZero(value))
            {
                // zero slots are not stored; absence reads back as zero
                storage.Remove(key);
            }
            else
            {
                storage.PutSpan(key, EvmWordSlot.AsReadOnlySpan(in value).WithoutLeadingZeros());
            }
        }

        public void SelfDestructStorage(Address address)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            Span<byte> firstKey = stackalloc byte[StorageKeyLength];
            addressHash.Bytes.CopyTo(firstKey);
            Span<byte> lastKeyExclusive = stackalloc byte[StorageKeyLength + 1];
            addressHash.Bytes.CopyTo(lastKeyExclusive);
            lastKeyExclusive[32..].Fill(0xFF);

            IWriteBatch storage = _batch.GetColumnBatch(PbtColumns.Storage);
            using ISortedView view = ((ISortedKeyValueStore)_preBatchSnapshot.GetColumn(PbtColumns.Storage)).GetViewBetween(firstKey, lastKeyExclusive);
            while (view.MoveNext())
            {
                storage.Remove(view.CurrentKey);
            }
        }

        public void SetLeafBlob(in Stem stem, byte[]? blob)
        {
            IWriteBatch leaves = _batch.GetColumnBatch(LeafColumn(stem));
            if (blob is null || blob.Length == 0)
            {
                leaves.Remove(stem.Bytes);
            }
            else
            {
                leaves.PutSpan(stem.Bytes, blob);
            }
        }

        public void SetTrieNode(in TrieNodeKey key, byte[]? node)
        {
            Span<byte> dbKey = stackalloc byte[TrieNodeKey.Length];
            key.WriteTo(dbKey);
            IWriteBatch nodes = _batch.GetColumnBatch(PbtColumns.TrieNodes);
            if (node is null)
            {
                nodes.Remove(dbKey);
            }
            else
            {
                nodes.PutSpan(dbKey, node);
            }
        }

        public void Dispose()
        {
            WriteCurrentState(_batch.GetColumnBatch(PbtColumns.Metadata), to);
            _batch.Dispose();
            _preBatchSnapshot.Dispose();
            db.Flush(onlyWal: true);
        }
    }
}
