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

/// <summary><see cref="IPbtPersistence"/> backed by the PBT columns database.</summary>
public class PbtRocksDbPersistence : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> LayoutVersionKey => "layoutVersion"u8;
    private static ReadOnlySpan<byte> TrieTilingKey => "trieTiling"u8;

    /// <summary>Block number, state root, and partition roots.</summary>
    private const int CurrentStateLength = sizeof(ulong) + ValueHash256.MemorySize + PbtPartitionRoots.EncodedLength;

    /// <summary>On-disk column layout version; increment it when key or value encodings change.</summary>
    private const int LayoutVersion = 6;

    private readonly IColumnsDb<PbtColumns> _db;

    public PbtRocksDbPersistence(IColumnsDb<PbtColumns> db, IPbtConfig config)
    {
        _db = db;
        EnsureLayoutVersion(db);
        EnsureTiling(db, config.TrieNodeLayout.Tiling());
    }

    /// <summary>Stamps a fresh database with <see cref="LayoutVersion"/> or rejects an incompatible layout.</summary>
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

        // A missing stamp is valid only for an empty database. Do not decode legacy state metadata.
        if (metadata.Get(CurrentStateKey) is not null)
        {
            throw new InvalidDataException($"The pbt database predates layout version {LayoutVersion} and cannot be read by this build. Delete the pbt database and re-import.");
        }

        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, LayoutVersion);
        metadata.PutSpan(LayoutVersionKey, value, WriteFlags.None);
    }

    /// <summary>Stamps a fresh database with its trie tiling or rejects an incompatible tiling.</summary>
    /// <exception cref="InvalidDataException">The database holds state under an unsupported or different tiling.</exception>
    private static void EnsureTiling(IColumnsDb<PbtColumns> db, PbtTiling tiling)
    {
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        byte[]? stored = metadata.Get(TrieTilingKey);
        if (stored is null)
        {
            if (ReadCurrentState(metadata).State != StateId.PreGenesis)
            {
                throw new InvalidDataException("The populated pbt database has no trie tiling stamp and may use the unsupported legacy clustered layout. Delete the pbt database and re-import.");
            }

            metadata.PutSpan(TrieTilingKey, [(byte)tiling], WriteFlags.None);
            return;
        }

        if (stored.Length != sizeof(byte) || !IsSupportedTiling(stored[0]))
        {
            throw new InvalidDataException($"The pbt database uses unsupported trie tiling stamp {Convert.ToHexString(stored)}. Delete the pbt database and re-import.");
        }

        PbtTiling storedTiling = (PbtTiling)stored[0];
        if (storedTiling != tiling)
        {
            throw new InvalidDataException($"The pbt database was written with the {storedTiling} trie tiling, but this node is configured for {tiling}. Delete the pbt database and re-import, or set Pbt.TrieNodeLayout to a layout of the {storedTiling} tiling.");
        }
    }

    private static bool IsSupportedTiling(byte tiling) => (PbtTiling)tiling is
        PbtTiling.SixLevel or PbtTiling.EightLevel or PbtTiling.FourLevel or PbtTiling.FiveLevel;

    private static PbtColumns LeafColumn(in Stem stem) => stem.Zone switch
    {
        0x0 => PbtColumns.AccountLeaves,
        0x1 => PbtColumns.CodeLeaves,
        >= 0x8 => PbtColumns.StorageLeaves,
        _ => throw new NotSupportedException($"Zone {stem.Zone} is reserved"),
    };

    private static PbtColumns TrieNodeColumn(in TrieNodeKey key) => key.Path.Zone switch
    {
        0x0 => PbtColumns.AccountTrieNodes,
        0x1 => PbtColumns.CodeTrieNodes,
        >= 0x8 => PbtColumns.StorageTrieNodes,
        _ => throw new NotSupportedException($"Zone {key.Path.Zone} is reserved"),
    };

    public IPbtPersistence.IReader CreateReader() => new Reader(_db.CreateSnapshot());

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in PbtPartitionRoots toPartitionRoots, WriteFlags flags)
    {
        StateId currentState = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata)).State;
        if (currentState != from)
        {
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {currentState}");
        }

        return new WriteBatch(_db, to, toPartitionRoots, flags);
    }

    public void Flush() => _db.Flush();

    internal static (StateId State, PbtPartitionRoots PartitionRoots) ReadCurrentState(IReadOnlyKeyValueStore metadata)
    {
        byte[]? value = metadata.Get(CurrentStateKey);
        return value is null
            ? (StateId.PreGenesis, PbtPartitionRoots.Empty)
            : (new StateId(BinaryPrimitives.ReadUInt64BigEndian(value), new ValueHash256(value.AsSpan(sizeof(ulong), ValueHash256.MemorySize))),
                PbtPartitionRoots.Decode(value.AsSpan(sizeof(ulong) + ValueHash256.MemorySize)));
    }

    private static void WriteCurrentState(IWriteOnlyKeyValueStore metadata, in StateId stateId, PbtPartitionRoots partitionRoots, WriteFlags flags)
    {
        Span<byte> value = stackalloc byte[CurrentStateLength];
        BinaryPrimitives.WriteUInt64BigEndian(value, stateId.BlockNumber);
        stateId.StateRoot.Bytes.CopyTo(value[sizeof(ulong)..]);
        partitionRoots.WriteTo(value[(sizeof(ulong) + ValueHash256.MemorySize)..]);
        metadata.PutSpan(CurrentStateKey, value, flags);
    }

    private sealed class Reader(IColumnDbSnapshot<PbtColumns> snapshot) : IPbtPersistence.IReader
    {
        private readonly (StateId State, PbtPartitionRoots PartitionRoots) _current = ReadCurrentState(snapshot.GetColumn(PbtColumns.Metadata));

        public StateId CurrentState => _current.State;

        public PbtPartitionRoots CurrentPartitionRoots => _current.PartitionRoots;

        public RefCountingMemory? GetLeafBlob(in Stem stem) => ReadOwned(snapshot.GetColumn(LeafColumn(stem)), stem.Bytes);

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
        {
            Span<byte> dbKey = stackalloc byte[TrieNodeKey.Length];
            key.WriteTo(dbKey);
            return ReadOwned(snapshot.GetColumn(TrieNodeColumn(key)), dbKey);
        }

        public ValueHash256? GetFullLeaf(PbtFullKey key)
        {
            byte[]? value = snapshot.GetColumn(PbtColumns.FullLeaves).Get(key.Bytes);
            if (value is null) return null;
            if (value.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT full-leaf value length.");
            return new ValueHash256(value);
        }

        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateFullLeaves()
        {
            ISortedKeyValueStore leaves = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.FullLeaves);
            using ISortedView view = leaves.GetViewBetween([], [0xFF, 0xFF]);
            while (view.MoveNext())
            {
                if (view.CurrentValue.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT full-leaf value length.");
                yield return new KeyValuePair<PbtFullKey, ValueHash256>(new PbtFullKey(view.CurrentKey), new ValueHash256(view.CurrentValue));
            }
        }

        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateFullLeaves(PbtFullKey prefix)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            ISortedKeyValueStore leaves = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.FullLeaves);
            byte[] upper = PrefixUpperBound(prefix.Bytes);
            using ISortedView view = leaves.GetViewBetween(prefix.Bytes, upper);
            while (view.MoveNext())
            {
                if (view.CurrentValue.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT full-leaf value length.");
                yield return new KeyValuePair<PbtFullKey, ValueHash256>(new PbtFullKey(view.CurrentKey), new ValueHash256(view.CurrentValue));
            }
        }

        private static byte[] PrefixUpperBound(ReadOnlySpan<byte> prefix)
        {
            byte[] upper = prefix.ToArray();
            for (int i = upper.Length - 1; i >= 0; i--)
            {
                if (++upper[i] != 0) return upper[..(i + 1)];
            }
            return new byte[PbtFullKey.MaxLength + 1];
        }

        private static RefCountingMemory? ReadOwned(IReadOnlyKeyValueStore column, scoped ReadOnlySpan<byte> key)
        {
            MemoryManager<byte>? memory = column.GetOwnedMemory(key);
            if (memory is null) return null;

            try
            {
                return RefCountingMemory.OwningRocksDb(memory);
            }
            catch
            {
                ((IDisposable)memory).Dispose();
                throw;
            }
        }

        public void Dispose() => snapshot.Dispose();
    }

    /// <remarks>Every operation carries <paramref name="flags"/> because the shared RocksDB batch uses its last write options.</remarks>
    private sealed class WriteBatch(IColumnsDb<PbtColumns> db, StateId to, PbtPartitionRoots toPartitionRoots, WriteFlags flags) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();

        public void SetLeafBlob(in Stem stem, scoped ReadOnlySpan<byte> blob)
        {
            IWriteBatch leaves = _batch.GetColumnBatch(LeafColumn(stem));
            if (blob.IsEmpty)
            {
                leaves.Set(stem.Bytes, null, flags);
            }
            else
            {
                leaves.PutSpan(stem.Bytes, blob, flags);
            }
        }

        public void SetTrieNode(in TrieNodeKey key, scoped ReadOnlySpan<byte> node)
        {
            Span<byte> dbKey = stackalloc byte[TrieNodeKey.Length];
            key.WriteTo(dbKey);
            IWriteBatch trieNodes = _batch.GetColumnBatch(TrieNodeColumn(key));
            if (node.IsEmpty)
            {
                trieNodes.Set(dbKey, null, flags);
            }
            else
            {
                trieNodes.PutSpan(dbKey, node, flags);
            }
        }

        public void SetFullLeaf(PbtFullKey key, ValueHash256? value)
        {
            IWriteBatch leaves = _batch.GetColumnBatch(PbtColumns.FullLeaves);
            if (value is null) leaves.Set(key.Bytes, null, flags);
            else leaves.PutSpan(key.Bytes, value.Value.Bytes, flags);
        }

        public void Dispose()
        {
            WriteCurrentState(_batch.GetColumnBatch(PbtColumns.Metadata), to, toPartitionRoots, flags);
            _batch.Dispose();

            // WAL-disabled batches become durable only when the caller flushes.
            if (!flags.HasFlag(WriteFlags.DisableWAL)) db.Flush(onlyWal: true);
        }
    }
}
