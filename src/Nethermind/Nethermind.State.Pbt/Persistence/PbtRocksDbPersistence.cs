// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// <see cref="IPbtPersistence"/> over the pbt columns database. Readers wrap a column-family
/// snapshot; write batches are atomic across all columns and record the new
/// <see cref="StateId"/> and its tree root in the metadata column of the same batch.
/// </summary>
public class PbtRocksDbPersistence : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> LayoutVersionKey => "layoutVersion"u8;
    private static ReadOnlySpan<byte> TrieTilingKey => "trieTiling"u8;

    /// <summary>Block number, then the header root the state is keyed by, then the EIP-8297 root it actually has.</summary>
    private const int CurrentStateLength = sizeof(ulong) + 2 * ValueHash256.MemorySize;

    /// <summary>
    /// On-disk layout of the columns this class writes. Bump it whenever a key or value encoding
    /// changes, so a database written by an older build is refused rather than silently misread.
    /// </summary>
    private const int LayoutVersion = 5;

    private readonly IColumnsDb<PbtColumns> _db;

    public PbtRocksDbPersistence(IColumnsDb<PbtColumns> db, IPbtConfig config)
    {
        _db = db;
        EnsureLayoutVersion(db);
        EnsureTiling(db, config.TrieNodeTiling);
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
        if (ReadCurrentState(metadata).State != StateId.PreGenesis)
        {
            throw new InvalidDataException($"The pbt database predates layout version {LayoutVersion} and cannot be read by this build. Delete the pbt database and re-import.");
        }

        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, LayoutVersion);
        metadata.PutSpan(LayoutVersionKey, value, WriteFlags.None);
    }

    /// <summary>
    /// Stamps a fresh database with the tiling it is about to be written in, and rejects one written
    /// in another.
    /// </summary>
    /// <remarks>
    /// The tiling fixes the keys a tree is stored under, so a database holds one and never both. A
    /// database with no stamp was written before the tilings parted, which is
    /// <see cref="PbtTiling.ClusteredFourLevel"/>.
    /// </remarks>
    /// <exception cref="InvalidDataException">The database holds state under another tiling.</exception>
    private static void EnsureTiling(IColumnsDb<PbtColumns> db, PbtTiling tiling)
    {
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        byte[]? stored = metadata.Get(TrieTilingKey);
        PbtTiling storedTiling = stored is null ? PbtTiling.ClusteredFourLevel : (PbtTiling)stored[0];
        if (stored is not null || ReadCurrentState(metadata).State != StateId.PreGenesis)
        {
            if (storedTiling != tiling)
            {
                throw new InvalidDataException($"The pbt database was written with the {storedTiling} trie tiling, but this node is configured for {tiling}. Delete the pbt database and re-import, or set Pbt.TrieNodeTiling to {storedTiling}.");
            }

            if (stored is not null) return;
        }

        metadata.PutSpan(TrieTilingKey, [(byte)tiling], WriteFlags.None);
    }

    private static PbtColumns LeafColumn(in Stem stem) => stem.Zone switch
    {
        0x0 => PbtColumns.AccountLeaves,
        0x1 => PbtColumns.CodeLeaves,
        >= 0x8 => PbtColumns.StorageLeaves,
        _ => throw new NotSupportedException($"Zone {stem.Zone} is reserved"),
    };

    /// <remarks>The depth-0 root's path is all zeros, so it falls into the account column.</remarks>
    private static PbtColumns TrieNodeColumn(in TrieNodeKey key) => key.Path.Zone switch
    {
        0x0 => PbtColumns.AccountTrieNodes,
        0x1 => PbtColumns.CodeTrieNodes,
        >= 0x8 => PbtColumns.StorageTrieNodes,
        _ => throw new NotSupportedException($"Zone {key.Path.Zone} is reserved"),
    };

    public IPbtPersistence.IReader CreateReader() => new Reader(_db.CreateSnapshot());

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 toTreeRoot, WriteFlags flags)
    {
        StateId currentState = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata)).State;
        if (currentState != from)
        {
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {currentState}");
        }

        return new WriteBatch(_db, to, toTreeRoot, flags);
    }

    public void Flush() => _db.Flush();

    internal static (StateId State, ValueHash256 TreeRoot) ReadCurrentState(IReadOnlyKeyValueStore metadata)
    {
        byte[]? value = metadata.Get(CurrentStateKey);
        return value is null
            ? (StateId.PreGenesis, default)
            : (new StateId(BinaryPrimitives.ReadUInt64BigEndian(value), new ValueHash256(value.AsSpan(sizeof(ulong), ValueHash256.MemorySize))),
                new ValueHash256(value.AsSpan(sizeof(ulong) + ValueHash256.MemorySize, ValueHash256.MemorySize)));
    }

    private static void WriteCurrentState(IWriteOnlyKeyValueStore metadata, in StateId stateId, in ValueHash256 treeRoot, WriteFlags flags)
    {
        Span<byte> value = stackalloc byte[CurrentStateLength];
        BinaryPrimitives.WriteUInt64BigEndian(value, stateId.BlockNumber);
        stateId.StateRoot.Bytes.CopyTo(value[sizeof(ulong)..]);
        treeRoot.Bytes.CopyTo(value[(sizeof(ulong) + ValueHash256.MemorySize)..]);
        metadata.PutSpan(CurrentStateKey, value, flags);
    }

    private sealed class Reader(IColumnDbSnapshot<PbtColumns> snapshot) : IPbtPersistence.IReader
    {
        private readonly (StateId State, ValueHash256 TreeRoot) _current = ReadCurrentState(snapshot.GetColumn(PbtColumns.Metadata));

        public StateId CurrentState => _current.State;

        public ValueHash256 CurrentTreeRoot => _current.TreeRoot;

        public RefCountingMemory? GetLeafBlob(in Stem stem) => ReadOwned(snapshot.GetColumn(LeafColumn(stem)), stem.Bytes);

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
        {
            Span<byte> dbKey = stackalloc byte[TrieNodeKey.Length];
            key.WriteTo(dbKey);
            return ReadOwned(snapshot.GetColumn(TrieNodeColumn(key)), dbKey);
        }

        /// <summary>
        /// Copies the store's native slice into an <see cref="ArrayPool{T}.Shared"/> rental that the
        /// caller returns to the pool on disposal.
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
    private sealed class WriteBatch(IColumnsDb<PbtColumns> db, StateId to, ValueHash256 toTreeRoot, WriteFlags flags) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();

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
            WriteCurrentState(_batch.GetColumnBatch(PbtColumns.Metadata), to, toTreeRoot, flags);
            _batch.Dispose();

            // a WAL-disabled batch is deliberately non-durable; the caller flushes when it is done
            if (!flags.HasFlag(WriteFlags.DisableWAL)) db.Flush(onlyWal: true);
        }
    }
}
