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

/// <summary><see cref="IPbtPersistence"/> backed by canonical PBT columns.</summary>
public class PbtRocksDbPersistence(
    IColumnsDb<PbtColumns> db,
    IPbtConfig config,
    IRefCountingMemoryProvider? memoryProvider = null) : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private static ReadOnlySpan<byte> ValidStateKey => "validState"u8;
    private const int CurrentStateLength = sizeof(ulong) + 2 * ValueHash256.MemorySize;
    private const int SchemaEpoch = 10;
    private const byte ValidState = 1;

    private readonly IColumnsDb<PbtColumns> _db = Initialize(db, config.ImportFromPreimageFlat);
    private readonly IRefCountingMemoryProvider _memoryProvider = memoryProvider ?? PooledRefCountingMemoryProvider.Instance;

    internal bool IsValid => _db.GetColumnDb(PbtColumns.Metadata).Get(ValidStateKey) is not null;

    private static IColumnsDb<PbtColumns> Initialize(IColumnsDb<PbtColumns> db, bool allowInterruptedImport)
    {
        EnsureSchema(db, allowInterruptedImport);
        return db;
    }

    private static void EnsureSchema(IColumnsDb<PbtColumns> db, bool allowInterruptedImport)
    {
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        byte[]? storedEpoch = metadata.Get(SchemaEpochKey);
        byte[]? storedCurrentState = metadata.Get(CurrentStateKey);
        byte[]? storedValidity = metadata.Get(ValidStateKey);

        if (storedEpoch is not null && storedEpoch.Length != sizeof(int))
            throw new InvalidDataException("Malformed PBT schema epoch. Delete the pbt database and rebuild or re-import.");
        ValidateCurrentState(storedCurrentState);
        ValidateValidity(storedValidity);

        if (storedEpoch is null)
        {
            if (storedCurrentState is not null || storedValidity is not null || HasPopulatedDataColumn(db))
            {
                throw new InvalidDataException($"The populated pbt database has no schema epoch {SchemaEpoch} stamp. Delete the pbt database and rebuild or re-import.");
            }

            Span<byte> value = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(value, SchemaEpoch);
            metadata.PutSpan(SchemaEpochKey, value, WriteFlags.None);
            return;
        }

        int epoch = BinaryPrimitives.ReadInt32BigEndian(storedEpoch);
        if (epoch != SchemaEpoch)
        {
            throw new InvalidDataException($"The pbt database uses schema epoch {epoch}, but this build reads epoch {SchemaEpoch}. Delete the pbt database and re-import.");
        }

        if ((storedCurrentState is null) != (storedValidity is null))
        {
            throw new InvalidDataException("The PBT validity marker and current-state metadata are inconsistent. Delete the pbt database and rebuild or re-import.");
        }

        if (storedValidity is not null) return;

        if (HasPopulatedDataColumn(db) && !allowInterruptedImport)
        {
            throw new InvalidDataException("The epoch-10 PBT database contains an interrupted initialization. Delete the pbt database and rebuild, or enable the preimage-flat import to clear and retry it.");
        }
    }

    private static bool HasPopulatedDataColumn(IColumnsDb<PbtColumns> db)
    {
        PbtColumns[] columns = [PbtColumns.FullLeaves, PbtColumns.NodeGroups, PbtColumns.CodeReferences,
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
        return new WriteBatch(_db, _memoryProvider, to, treeRoot, flags, publishState: true);
    }

    public IPbtPersistence.IWriteBatch CreateStagingWriteBatch(WriteFlags flags) =>
        new WriteBatch(_db, _memoryProvider, default, default, flags, publishState: false);

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
        if (value is not null && value.Length != CurrentStateLength)
            throw new InvalidDataException("Malformed PBT current-state metadata. Delete the pbt database and rebuild or re-import.");
    }

    private static void ValidateValidity(byte[]? value)
    {
        if (value is not [ValidState])
        {
            if (value is not null)
                throw new InvalidDataException("Malformed PBT validity metadata. Delete the pbt database and rebuild or re-import.");
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

        public PbtNodeGroupPayload? GetNodeGroup(PbtNodePath groupKey)
        {
            ArgumentNullException.ThrowIfNull(groupKey);
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));

            MemoryManager<byte>? owned = snapshot.GetColumn(PbtColumns.NodeGroups).GetOwnedMemory(groupKey.Encode());
            return owned is null ? null : PbtNodeGroupPayload.FromLease(RefCountingMemory.OwningRocksDb(owned));
        }

        public byte[]? GetNode(PbtNodePath path)
        {
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
            IReadOnlyKeyValueStore groups = snapshot.GetColumn(PbtColumns.NodeGroups);
            Span<byte> payload = groups.GetSpan(location.GroupKey.Encode());
            if (payload.IsNull()) return null;

            try
            {
                PbtNodeGroupReader reader = new(location.GroupKey, payload);
                return reader.TryGetNode(location.Position, out ReadOnlySpan<byte> encoding) ? encoding.ToArray() : null;
            }
            finally
            {
                groups.DangerousReleaseMemory(payload);
            }
        }

        public IEnumerable<KeyValuePair<PbtNodePath, byte[]>> EnumerateNodes()
        {
            ISortedKeyValueStore groups = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.NodeGroups);
            List<KeyValuePair<PbtNodePath, byte[]>> nodes = [];
            using (ISortedView view = groups.GetViewBetween([], [0xFF, 0xFF]))
            {
                while (view.MoveNext())
                {
                    PbtNodePath groupKey = DecodeGroupKey(view.CurrentKey);
                    PbtNodeGroupReader reader = new(groupKey, view.CurrentValue);
                    PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
                    while (enumerator.MoveNext())
                    {
                        PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, enumerator.CurrentPosition);
                        nodes.Add(new KeyValuePair<PbtNodePath, byte[]>(path, enumerator.Current.ToArray()));
                    }
                }
            }

            nodes.Sort(static (left, right) => left.Key.CompareTo(right.Key));
            return nodes;
        }

        private static PbtNodePath DecodeGroupKey(ReadOnlySpan<byte> encoding)
        {
            PbtNodePath groupKey = PbtNodePath.Decode(encoding);
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new InvalidDataException("A persisted PBT node-group key depth must be a four-level boundary.");
            return groupKey;
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

    private sealed class WriteBatch(
        IColumnsDb<PbtColumns> db,
        IRefCountingMemoryProvider memoryProvider,
        StateId to,
        ValueHash256 root,
        WriteFlags flags,
        bool publishState) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();
        private readonly Dictionary<PbtNodePath, Dictionary<int, byte[]?>> _nodeMutations = [];

        public void SetLeaf(PbtFullKey key, ValueHash256? value)
        {
            IWriteBatch leaves = _batch.GetColumnBatch(PbtColumns.FullLeaves);
            if (value is null) leaves.Set(key.Bytes, null, flags);
            else leaves.PutSpan(key.Bytes, value.Value.Bytes, flags);
        }

        public void SetNode(PbtNodePath path, ReadOnlySpan<byte> encoding)
        {
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
            if (!_nodeMutations.TryGetValue(location.GroupKey, out Dictionary<int, byte[]?>? mutations))
            {
                mutations = [];
                _nodeMutations.Add(location.GroupKey, mutations);
            }

            if (!encoding.IsEmpty) _ = PbtNodeCodec.Decode(encoding);
            mutations[location.Position] = encoding.IsEmpty ? null : encoding.ToArray();
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

        private bool _completed;

        public void Commit()
        {
            if (_completed) return;
            try
            {
                ApplyNodeMutations();
                if (publishState)
                {
                    Span<byte> value = stackalloc byte[CurrentStateLength];
                    BinaryPrimitives.WriteUInt64BigEndian(value, to.BlockNumber);
                    to.StateRoot.Bytes.CopyTo(value[sizeof(ulong)..]);
                    root.Bytes.CopyTo(value[(sizeof(ulong) + ValueHash256.MemorySize)..]);
                    IWriteBatch metadata = _batch.GetColumnBatch(PbtColumns.Metadata);
                    metadata.PutSpan(CurrentStateKey, value, flags);
                    metadata.PutSpan(ValidStateKey, [ValidState], flags);
                }
            }
            catch
            {
                _completed = true;
                _batch.Clear();
                _batch.Dispose();
                throw;
            }

            _completed = true;
            _batch.Dispose();
            if (!flags.HasFlag(WriteFlags.DisableWAL)) db.Flush(onlyWal: true);
        }

        private void ApplyNodeMutations()
        {
            if (_nodeMutations.Count == 0) return;

            IWriteBatch groupsBatch = _batch.GetColumnBatch(PbtColumns.NodeGroups);
            IDb groups = db.GetColumnDb(PbtColumns.NodeGroups);
            foreach ((PbtNodePath groupKey, Dictionary<int, byte[]?> mutations) in _nodeMutations)
            {
                Dictionary<int, byte[]> nodes = [];
                byte[] encodedGroupKey = groupKey.Encode();
                Span<byte> priorPayload = groups.GetSpan(encodedGroupKey);
                if (!priorPayload.IsNull())
                {
                    try
                    {
                        PbtNodeGroupReader reader = new(groupKey, priorPayload);
                        PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
                        while (enumerator.MoveNext()) nodes[enumerator.CurrentPosition] = enumerator.Current.ToArray();
                    }
                    finally
                    {
                        groups.DangerousReleaseMemory(priorPayload);
                    }
                }

                foreach ((int position, byte[]? encoding) in mutations)
                {
                    if (encoding is null)
                    {
                        nodes.Remove(position);
                    }
                    else
                    {
                        _ = PbtNodeCodec.Decode(encoding);
                        nodes[position] = encoding;
                    }
                }

                if (nodes.Count == 0)
                {
                    groupsBatch.Set(encodedGroupKey, null, flags);
                    continue;
                }

                List<PbtNodeRecord> records = new(nodes.Count);
                foreach ((int position, byte[] encoding) in nodes)
                    records.Add(new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, position), encoding));

                BufferWriter writer = new(memoryProvider);
                RefCountingMemory? encodedGroup = null;
                try
                {
                    PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
                    encodedGroup = writer.Detach();
                    groupsBatch.PutSpan(encodedGroupKey, encodedGroup!.GetSpan(), flags);
                }
                finally
                {
                    ((IDisposable?)encodedGroup)?.Dispose();
                    writer.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (_completed) return;
            _completed = true;
            _batch.Clear();
            _batch.Dispose();
        }
    }
}
