// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary><see cref="IPbtPersistence"/> backed by canonical PBT columns.</summary>
public class PbtRocksDbPersistence : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private const int CurrentStateLength = sizeof(ulong) + 2 * ValueHash256.MemorySize;
    private const int SchemaEpoch = 7;

    private readonly IColumnsDb<PbtColumns> _db;

    public PbtRocksDbPersistence(IColumnsDb<PbtColumns> db, IPbtConfig config)
    {
        _db = db;
        EnsureSchema(db);
    }

    private static void EnsureSchema(IColumnsDb<PbtColumns> db)
    {
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        byte[]? stored = metadata.Get(SchemaEpochKey);
        if (stored is not null)
        {
            if (stored.Length != sizeof(int)) throw new InvalidDataException("Malformed PBT schema epoch.");
            int epoch = BinaryPrimitives.ReadInt32BigEndian(stored);
            if (epoch != SchemaEpoch)
            {
                throw new InvalidDataException($"The pbt database uses schema epoch {epoch}, but this build reads epoch {SchemaEpoch}. Delete the pbt database and re-import.");
            }
            ValidateCurrentState(metadata.Get(CurrentStateKey));
            return;
        }

        if (HasPopulatedColumn(db))
        {
            throw new InvalidDataException($"The populated pbt database has no schema epoch {SchemaEpoch} stamp. Delete the pbt database and re-import.");
        }

        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, SchemaEpoch);
        metadata.PutSpan(SchemaEpochKey, value, WriteFlags.None);
    }

    private static bool HasPopulatedColumn(IColumnsDb<PbtColumns> db)
    {
        if (db.GetColumnDb(PbtColumns.Metadata).Get(CurrentStateKey) is not null) return true;
        PbtColumns[] columns = [PbtColumns.FullLeaves, PbtColumns.CompressedNodes, PbtColumns.CodeReferences,
            PbtColumns.AccountLeaves, PbtColumns.CodeLeaves, PbtColumns.StorageLeaves,
            PbtColumns.AccountTrieNodes, PbtColumns.CodeTrieNodes, PbtColumns.StorageTrieNodes];
        foreach (PbtColumns column in columns)
        {
            if (db.GetColumnDb(column).GetAll().GetEnumerator().MoveNext()) return true;
        }
        return false;
    }

    public IPbtPersistence.IReader CreateReader() => new Reader(_db.CreateSnapshot());

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags)
    {
        StateId current = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata)).State;
        if (current != from) throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {current}");
        return new WriteBatch(_db, to, treeRoot, flags);
    }

    public void Flush() => _db.Flush();

    internal static (StateId State, ValueHash256 Root) ReadCurrentState(IReadOnlyKeyValueStore metadata)
    {
        byte[]? value = metadata.Get(CurrentStateKey);
        if (value is null) return (StateId.PreGenesis, default);
        ValidateCurrentState(value);
        return (new StateId(BinaryPrimitives.ReadUInt64BigEndian(value), new ValueHash256(value.AsSpan(sizeof(ulong), ValueHash256.MemorySize))),
            new ValueHash256(value.AsSpan(sizeof(ulong) + ValueHash256.MemorySize)));
    }

    private static void ValidateCurrentState(byte[]? value)
    {
        if (value is not null && value.Length != CurrentStateLength) throw new InvalidDataException("Malformed PBT current-state metadata.");
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

    private sealed class Reader(IColumnDbSnapshot<PbtColumns> snapshot) : IPbtPersistence.IReader
    {
        private readonly (StateId State, ValueHash256 Root) _current = ReadCurrentState(snapshot.GetColumn(PbtColumns.Metadata));

        public StateId CurrentState => _current.State;
        public ValueHash256 CurrentRoot => _current.Root;

        public ValueHash256? GetLeaf(PbtFullKey key)
        {
            byte[]? value = snapshot.GetColumn(PbtColumns.FullLeaves).Get(key.Bytes);
            if (value is null) return null;
            if (value.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT leaf value length.");
            return new ValueHash256(value);
        }

        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => EnumerateLeavesCore([], [0xFF, 0xFF]);

        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) =>
            EnumerateLeavesCore(prefix.Bytes.ToArray(), PrefixUpperBound(prefix.Bytes));

        private IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeavesCore(byte[] lower, byte[] upper)
        {
            ISortedKeyValueStore leaves = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.FullLeaves);
            using ISortedView view = leaves.GetViewBetween(lower, upper);
            while (view.MoveNext())
            {
                if (view.CurrentValue.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT leaf value length.");
                yield return new KeyValuePair<PbtFullKey, ValueHash256>(new PbtFullKey(view.CurrentKey), new ValueHash256(view.CurrentValue));
            }
        }

        public byte[]? GetNode(PbtFullKey locator) => snapshot.GetColumn(PbtColumns.CompressedNodes).Get(locator.Bytes);

        public IEnumerable<KeyValuePair<PbtFullKey, byte[]>> EnumerateNodes()
        {
            ISortedKeyValueStore nodes = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.CompressedNodes);
            using ISortedView view = nodes.GetViewBetween([], [0xFF, 0xFF]);
            while (view.MoveNext())
                yield return new KeyValuePair<PbtFullKey, byte[]>(new PbtFullKey(view.CurrentKey), view.CurrentValue.ToArray());
        }

        public ulong GetCodeReference(in ValueHash256 codeHash)
        {
            byte[]? value = snapshot.GetColumn(PbtColumns.CodeReferences).Get(codeHash.Bytes);
            if (value is null) return 0;
            if (value.Length != sizeof(ulong)) throw new InvalidDataException("Invalid persisted PBT code-reference value length.");
            return BinaryPrimitives.ReadUInt64BigEndian(value);
        }

        public void Dispose() => snapshot.Dispose();
    }

    private sealed class WriteBatch(IColumnsDb<PbtColumns> db, StateId to, ValueHash256 root, WriteFlags flags) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();

        public void SetLeaf(PbtFullKey key, ValueHash256? value)
        {
            IWriteBatch leaves = _batch.GetColumnBatch(PbtColumns.FullLeaves);
            if (value is null) leaves.Set(key.Bytes, null, flags);
            else leaves.PutSpan(key.Bytes, value.Value.Bytes, flags);
        }

        public void SetNode(PbtFullKey locator, ReadOnlySpan<byte> encoding)
        {
            IWriteBatch nodes = _batch.GetColumnBatch(PbtColumns.CompressedNodes);
            if (encoding.IsEmpty) nodes.Set(locator.Bytes, null, flags);
            else nodes.PutSpan(locator.Bytes, encoding, flags);
        }

        public void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount)
        {
            IWriteBatch references = _batch.GetColumnBatch(PbtColumns.CodeReferences);
            if (referenceCount is null or 0)
            {
                references.Set(codeHash.Bytes, null, flags);
                return;
            }
            Span<byte> value = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(value, referenceCount.Value);
            references.PutSpan(codeHash.Bytes, value, flags);
        }

        public void Dispose()
        {
            Span<byte> value = stackalloc byte[CurrentStateLength];
            BinaryPrimitives.WriteUInt64BigEndian(value, to.BlockNumber);
            to.StateRoot.Bytes.CopyTo(value[sizeof(ulong)..]);
            root.Bytes.CopyTo(value[(sizeof(ulong) + ValueHash256.MemorySize)..]);
            _batch.GetColumnBatch(PbtColumns.Metadata).PutSpan(CurrentStateKey, value, flags);
            _batch.Dispose();
            if (!flags.HasFlag(WriteFlags.DisableWAL)) db.Flush(onlyWal: true);
        }
    }
}
