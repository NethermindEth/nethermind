// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
public class PbtRocksDbPersistence : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> LayoutVersionKey => "layoutVersion"u8;

    /// <summary>
    /// On-disk layout of the keys this class encodes. Bump it whenever a key encoding changes, so a
    /// database written by an older build is refused rather than silently misread.
    /// </summary>
    /// <remarks>
    /// Version 1 keys flat storage by the slot's EIP-8297 tree key. Databases predating the version
    /// key keyed it by <c>blake3(address32) || slot32BE</c>; every slot in one would read back as
    /// absent, i.e. zero, and the rebuilt root would be wrong with nothing to signal it — hence the
    /// refusal rather than a best-effort open.
    ///
    /// Version 2 moved the trie node key's depth from the leading byte to the trailing one and split
    /// the nodes into per-zone columns. A version 1 database read under it would resolve every node
    /// lookup to a wrong key in a column that no longer holds it.
    /// </remarks>
    private const int LayoutVersion = 2;

    private readonly IColumnsDb<PbtColumns> _db;

    public PbtRocksDbPersistence(IColumnsDb<PbtColumns> db)
    {
        _db = db;
        EnsureLayoutVersion(db);
    }

    /// <summary>Stamps a fresh database with <see cref="LayoutVersion"/>, and rejects one written under any other layout.</summary>
    /// <exception cref="InvalidDataException">The database holds state under a layout this build cannot read.</exception>
    private static void EnsureLayoutVersion(IColumnsDb<PbtColumns> db)
    {
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        byte[]? stored = metadata.Get(LayoutVersionKey);
        if (stored is not null)
        {
            int version = BinaryPrimitives.ReadInt32BigEndian(stored);
            if (version != LayoutVersion)
            {
                throw new InvalidDataException($"The pbt database was written with layout version {version}, but this build reads version {LayoutVersion}. Delete the pbt database and re-import.");
            }

            return;
        }

        // No version key: either a fresh database, which we stamp, or one written before the key
        // existed. Only the latter holds state, so the persisted-state pointer tells them apart.
        if (ReadCurrentState(metadata) != StateId.PreGenesis)
        {
            throw new InvalidDataException($"The pbt database predates layout version {LayoutVersion} and cannot be read by this build. Delete the pbt database and re-import.");
        }

        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, LayoutVersion);
        metadata.PutSpan(LayoutVersionKey, value, WriteFlags.None);
    }

    /// <summary>Maps a stem to its leaf-blob column by zone (the tree layer is column-agnostic, so the routing lives here).</summary>
    private static PbtColumns LeafColumn(in Stem stem) => stem.Zone switch
    {
        0x0 => PbtColumns.AccountLeaves,
        0x1 => PbtColumns.CodeLeaves,
        >= 0x8 => PbtColumns.StorageLeaves,
        _ => throw new NotSupportedException($"Zone {stem.Zone} is reserved"),
    };

    /// <summary>Maps a trie node to its column by zone, mirroring <see cref="LeafColumn"/>.</summary>
    /// <remarks>The depth-0 root's path is all zeros, so it falls into the account column.</remarks>
    private static PbtColumns TrieNodeColumn(in TrieNodeKey key) => key.Path.Zone switch
    {
        0x0 => PbtColumns.AccountTrieNodes,
        0x1 => PbtColumns.CodeTrieNodes,
        >= 0x8 => PbtColumns.StorageTrieNodes,
        _ => throw new NotSupportedException($"Zone {key.Path.Zone} is reserved"),
    };

    public IPbtPersistence.IReader CreateReader() => new Reader(_db.CreateSnapshot());

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        StateId currentState = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata));
        if (currentState != from)
        {
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {currentState}");
        }

        return new WriteBatch(_db, to, flags);
    }

    public void Flush() => _db.Flush();

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore metadata)
    {
        byte[]? value = metadata.Get(CurrentStateKey);
        return value is null
            ? StateId.PreGenesis
            : new StateId(BinaryPrimitives.ReadUInt64BigEndian(value), new ValueHash256(value.AsSpan(8, 32)));
    }

    private static void WriteCurrentState(IWriteOnlyKeyValueStore metadata, in StateId stateId, WriteFlags flags)
    {
        Span<byte> value = stackalloc byte[40];
        BinaryPrimitives.WriteUInt64BigEndian(value, stateId.BlockNumber);
        stateId.StateRoot.Bytes.CopyTo(value[8..]);
        metadata.PutSpan(CurrentStateKey, value, flags);
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
            ValueHash256 key = PbtKeyDerivation.StorageKey(address, slot);
            byte[]? stored = snapshot.GetColumn(PbtColumns.Storage).Get(key.Bytes);
            return stored is null ? default : EvmWordSlot.FromStripped(stored);
        }

        public RefCountingMemory? GetLeafBlob(in Stem stem) => ReadOwned(snapshot.GetColumn(LeafColumn(stem)), stem.Bytes);

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
        {
            Span<byte> dbKey = stackalloc byte[TrieNodeKey.Length];
            key.WriteTo(dbKey);
            return ReadOwned(snapshot.GetColumn(TrieNodeColumn(key)), dbKey);
        }

        /// <summary>
        /// Reads a value into a pooled buffer: copies the store's (native) slice into an
        /// <see cref="ArrayPool{T}.Shared"/> rental that the caller returns to the pool on disposal.
        /// PBT never stores an empty value, so an empty/absent slice is treated as absent.
        /// </summary>
        private static RefCountingMemory? ReadOwned(IReadOnlyKeyValueStore column, scoped ReadOnlySpan<byte> key)
        {
            Span<byte> value = column.GetSpan(key);
            if (value.IsNull()) return null;

            try
            {
                int length = value.Length;
                byte[] rented = ArrayPool<byte>.Shared.Rent(length);
                value.CopyTo(rented);
                return RefCountingMemory.Owning(rented, length);
            }
            finally
            {
                column.DangerousReleaseMemory(value);
            }
        }

        public void Dispose() => snapshot.Dispose();
    }

    /// <remarks>
    /// All columns share one underlying RocksDB batch whose write options are taken from its last
    /// write, so every write here — deletes included, hence <c>Set(key, null, flags)</c> over
    /// <c>Remove(key)</c> — must carry <paramref name="flags"/> for them to take effect.
    /// </remarks>
    private sealed class WriteBatch(IColumnsDb<PbtColumns> db, StateId to, WriteFlags flags) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();

        // Storage keys are tree keys, so each one costs two hashes to derive from scratch. Callers that
        // write an address's slots together — the persistence coordinator and the importer both group
        // them — collapse that to one hash per address plus one per 256-slot run.
        private PbtSlotKeyDeriver _deriver;
        private Address? _deriverAddress;

        public void SetAccount(Address address, Account? account)
        {
            ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
            IWriteBatch accounts = _batch.GetColumnBatch(PbtColumns.Account);
            if (account is null)
            {
                accounts.Set(addressHash.Bytes, null, flags);
            }
            else
            {
                using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
                accounts.PutSpan(addressHash.Bytes, rlp, flags);
            }
        }

        public void SetSlot(Address address, in UInt256 slot, in EvmWord value)
        {
            if (!address.Equals(_deriverAddress))
            {
                _deriver = new PbtSlotKeyDeriver(address);
                _deriverAddress = address;
            }

            ValueHash256 key = _deriver.TreeKey(slot);
            IWriteBatch storage = _batch.GetColumnBatch(PbtColumns.Storage);
            if (EvmWordSlot.IsZero(value))
            {
                // zero slots are not stored; absence reads back as zero
                storage.Set(key.Bytes, null, flags);
            }
            else
            {
                storage.PutSpan(key.Bytes, EvmWordSlot.AsReadOnlySpan(in value).WithoutLeadingZeros(), flags);
            }
        }

        public void SetLeafBlob(in Stem stem, byte[]? blob)
        {
            IWriteBatch leaves = _batch.GetColumnBatch(LeafColumn(stem));
            if (blob is null || blob.Length == 0)
            {
                leaves.Set(stem.Bytes, null, flags);
            }
            else
            {
                leaves.PutSpan(stem.Bytes, blob, flags);
            }
        }

        public void SetTrieNode(in TrieNodeKey key, byte[]? node)
        {
            Span<byte> dbKey = stackalloc byte[TrieNodeKey.Length];
            key.WriteTo(dbKey);
            IWriteBatch nodes = _batch.GetColumnBatch(TrieNodeColumn(key));
            if (node is null)
            {
                nodes.Set(dbKey, null, flags);
            }
            else
            {
                nodes.PutSpan(dbKey, node, flags);
            }
        }

        public void Dispose()
        {
            WriteCurrentState(_batch.GetColumnBatch(PbtColumns.Metadata), to, flags);
            _batch.Dispose();

            // a WAL-disabled batch is deliberately non-durable; the caller flushes when it is done
            if (!flags.HasFlag(WriteFlags.DisableWAL)) db.Flush(onlyWal: true);
        }
    }
}
