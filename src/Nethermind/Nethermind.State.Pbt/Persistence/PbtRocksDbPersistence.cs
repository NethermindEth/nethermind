// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Pbt;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Pbt.Persistence;

/// <summary><see cref="IPbtPersistence"/> backed by canonical PBT columns.</summary>
public class PbtRocksDbPersistence(
    IColumnsDb<PbtColumns> db,
    IPbtConfig config) : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private static ReadOnlySpan<byte> ValidStateKey => "validState"u8;
    private const int CurrentStateLength = sizeof(ulong) + 2 * ValueHash256.MemorySize;
    internal static ReadOnlySpan<byte> RootNodeGroupKey => "rootNodeGroup"u8;
    private const int SchemaEpoch = 12;
    private const byte ValidState = 1;

    private readonly IColumnsDb<PbtColumns> _db = Initialize(db, config.ImportFromPreimageFlat);

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
            throw new InvalidDataException("Malformed PBT schema epoch. Rebuild or re-import into a new pbt database.");
        ValidateCurrentState(storedCurrentState);
        ValidateValidity(storedValidity);

        if (storedEpoch is null)
        {
            if (storedCurrentState is not null || storedValidity is not null || HasPopulatedDataColumn(db))
            {
                throw new InvalidDataException($"The populated pbt database has no schema epoch {SchemaEpoch} stamp. Rebuild or re-import into a new pbt database.");
            }

            Span<byte> value = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(value, SchemaEpoch);
            metadata.PutSpan(SchemaEpochKey, value, WriteFlags.None);
            return;
        }

        int epoch = BinaryPrimitives.ReadInt32BigEndian(storedEpoch);
        if (epoch != SchemaEpoch)
        {
            throw new InvalidDataException($"The pbt database uses schema epoch {epoch}, but this build reads epoch {SchemaEpoch}. Rebuild or re-import into a new pbt database.");
        }

        if ((storedCurrentState is null) != (storedValidity is null))
        {
            throw new InvalidDataException("The PBT validity marker and current-state metadata are inconsistent. Rebuild or re-import into a new pbt database.");
        }

        if (storedValidity is not null) return;

        if (HasPopulatedDataColumn(db) && !allowInterruptedImport)
        {
            throw new InvalidDataException($"The epoch-{SchemaEpoch} PBT database contains an interrupted initialization. Rebuild into a new pbt database, or enable the preimage-flat import to clear and retry it.");
        }
    }

    private static bool HasPopulatedDataColumn(IColumnsDb<PbtColumns> db)
    {
        if (db.GetColumnDb(PbtColumns.Metadata).Get(RootNodeGroupKey) is not null) return true;

        PbtColumns[] columns = [PbtColumns.FullLeaves, PbtColumns.CodeReferences,
            PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups,
            PbtColumns.AccountLeaves, PbtColumns.CodeLeaves, PbtColumns.StorageLeaves,
            PbtColumns.AccountTrieNodes, PbtColumns.CodeTrieNodes, PbtColumns.StorageTrieNodes,
            PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes];
        foreach (PbtColumns column in columns)
        {
            using IEnumerator<KeyValuePair<byte[], byte[]>> entries = db.GetColumnDb(column).GetAll().GetEnumerator();
            if (entries.MoveNext()) return true;
        }
        return false;
    }

    public IPbtPersistence.IReader CreateReader() => new Reader(_db.CreateSnapshot());

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags)
    {
        StateId current = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata)).State;
        if (current != from) throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {current}");
        return new WriteBatch(_db, to, treeRoot, flags, publishState: true);
    }

    public IPbtPersistence.IWriteBatch CreateStagingWriteBatch(WriteFlags flags) =>
        new WriteBatch(_db, default, default, flags, publishState: false);

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
            throw new InvalidDataException("Malformed PBT current-state metadata. Rebuild or re-import into a new pbt database.");
    }

    private static void ValidateValidity(byte[]? value)
    {
        if (value is not [ValidState])
        {
            if (value is not null)
                throw new InvalidDataException("Malformed PBT validity metadata. Rebuild or re-import into a new pbt database.");
        }
    }

    private static PbtColumns NodeGroupColumn<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        if (groupKey.BitDepth == 0) return PbtColumns.Metadata;
        if (groupKey.BitDepth == 4 && groupKey.GetByte(0) == 0xF0
            || groupKey.BitDepth >= 8 && groupKey.GetByte(0) == Eip8297KeyDerivation.StorageZone)
            return PbtColumns.StorageNodeGroups;
        if (groupKey.BitDepth >= 8 && groupKey.GetByte(0) == Eip8297KeyDerivation.CodeZone)
            return PbtColumns.CodeNodeGroups;
        return PbtColumns.AccountNodeGroups;
    }

    private static ReadOnlySpan<byte> NodeGroupStorageKey<TPath>(TPath groupKey, Span<byte> destination) where TPath : struct, IPbtNodePath<TPath>
    {
        if (groupKey.BitDepth == 0) return RootNodeGroupKey;
        groupKey.Encode(destination);
        return destination[..groupKey.EncodedLength];
    }

    private static byte[] PrefixUpperBound(ReadOnlySpan<byte> prefix)
    {
        byte[] upper = prefix.ToArray();
        for (int i = upper.Length - 1; i >= 0; i--)
        {
            if (++upper[i] != 0) return upper[..(i + 1)];
        }
        byte[] maximum = new byte[PbtStorageFullKey.MaxLength + 1];
        Array.Fill(maximum, byte.MaxValue);
        return maximum;
    }

    private sealed class Reader(IColumnDbSnapshot<PbtColumns> snapshot) : IPbtPersistence.IReader
    {
        private readonly (StateId State, ValueHash256 Root) _current = ReadCurrentState(snapshot.GetColumn(PbtColumns.Metadata));

        public StateId CurrentState => _current.State;
        public ValueHash256 CurrentRoot => _current.Root;

        public Account? GetAccount(in ValueHash256 addressHash)
        {
            byte[]? value = snapshot.GetColumn(PbtColumns.Accounts).Get(addressHash.Bytes);
            return value is null ? null : DecodeAccount(value);
        }

        public EvmWord GetSlot(PbtStorageFullKey key)
        {
            byte[]? value = snapshot.GetColumn(PbtColumns.Storages).Get(key.Bytes);
            return value is null ? default : DecodeSlot(value);
        }

        public CodeInfo? GetCode(in ValueHash256 codeHash)
        {
            byte[]? value = snapshot.GetColumn(PbtColumns.Codes).Get(codeHash.Bytes);
            return value is null ? null : new CodeInfo(value) { CodeHash = codeHash };
        }

        public IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts()
        {
            ISortedKeyValueStore accounts = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.Accounts);
            byte[] upper = new byte[ValueHash256.MemorySize + 1];
            Array.Fill(upper, byte.MaxValue);
            using ISortedView view = accounts.GetViewBetween([], upper);
            while (view.MoveNext())
                yield return new(new ValueHash256(view.CurrentKey), DecodeAccount(view.CurrentValue));
        }

        public IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(PbtStorageFullKey? prefix = null)
        {
            ISortedKeyValueStore storage = (ISortedKeyValueStore)snapshot.GetColumn(PbtColumns.Storages);
            using ISortedView view = storage.GetViewBetween(prefix is null ? [] : prefix.Value.Bytes,
                PrefixUpperBound(prefix is null ? [] : prefix.Value.Bytes));
            while (view.MoveNext())
                yield return new(new PbtStorageFullKey(view.CurrentKey), DecodeSlot(view.CurrentValue));
        }

        private static Account DecodeAccount(ReadOnlySpan<byte> value)
        {
            RlpReader reader = new(value);
            return AccountDecoder.Instance.Decode(ref reader) ?? throw new InvalidDataException("Invalid persisted PBT account.");
        }

        private static EvmWord DecodeSlot(ReadOnlySpan<byte> value)
        {
            if (value.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT storage value length.");
            return EvmWordSlot.FromStripped(value);
        }

        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
        {
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));

            Span<byte> key = stackalloc byte[groupKey.EncodedLength];
            MemoryManager<byte>? owned = snapshot.GetColumn(NodeGroupColumn(groupKey)).GetOwnedMemory(NodeGroupStorageKey(groupKey, key));
            return owned is null ? null : RefCountingMemory.OwningRocksDb(owned);
        }

        public IEnumerable<PbtStorageNodePath> EnumerateNodeGroupKeys()
        {
            if (snapshot.GetColumn(PbtColumns.Metadata).Get(RootNodeGroupKey) is not null)
                yield return PbtStorageNodePath.Create([], 0);

            using ISortedView accounts = OpenGroups(PbtColumns.AccountNodeGroups);
            using ISortedView codes = OpenGroups(PbtColumns.CodeNodeGroups);
            using ISortedView storage = OpenGroups(PbtColumns.StorageNodeGroups);
            bool hasAccount = accounts.MoveNext();
            bool hasCode = codes.MoveNext();
            bool hasStorage = storage.MoveNext();
            while (hasAccount || hasCode || hasStorage)
            {
                ISortedView next = hasAccount ? accounts : hasCode ? codes : storage;
                if (hasCode && codes.CurrentKey.SequenceCompareTo(next.CurrentKey) < 0) next = codes;
                if (hasStorage && storage.CurrentKey.SequenceCompareTo(next.CurrentKey) < 0) next = storage;
                yield return DecodeGroupKey(next.CurrentKey);
                if (ReferenceEquals(next, accounts)) hasAccount = accounts.MoveNext();
                else if (ReferenceEquals(next, codes)) hasCode = codes.MoveNext();
                else hasStorage = storage.MoveNext();
            }
        }

        private ISortedView OpenGroups(PbtColumns column)
        {
            ISortedKeyValueStore groups = (ISortedKeyValueStore)snapshot.GetColumn(column);
            return groups.GetViewBetween([], [0xFF, 0xFF]);
        }

        private static PbtStorageNodePath DecodeGroupKey(ReadOnlySpan<byte> encoding)
        {
            PbtStorageNodePath groupKey = PbtStorageNodePath.Decode(encoding);
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
        StateId to,
        ValueHash256 root,
        WriteFlags flags,
        bool publishState) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();

        private readonly Dictionary<ValueHash256, HashSet<PbtStorageFullKey>> _stagedStorageKeys = [];

        public void SetAccount(in ValueHash256 addressHash, Account? account)
        {
            IWriteBatch accounts = _batch.GetColumnBatch(PbtColumns.Accounts);
            if (account is null) accounts.Set(addressHash.Bytes, null, flags);
            else
            {
                using ArrayPoolSpan<byte> encoded = AccountDecoder.Instance.EncodeToArrayPoolSpan(account);
                accounts.PutSpan(addressHash.Bytes, encoded, flags);
            }
        }

        public void SetSlot(PbtStorageFullKey key, in EvmWord value)
        {
            if (!IsStorageKey(key.Bytes)) throw new ArgumentException("A complete storage key is required.", nameof(key));
            IWriteBatch storage = _batch.GetColumnBatch(PbtColumns.Storages);
            if (EvmWordSlot.IsZero(value)) storage.Set(key.Bytes, null, flags);
            else storage.PutSpan(key.Bytes, EvmWordSlot.AsReadOnlySpan(in value), flags);
            ValueHash256 addressHash = new(key.Bytes.Slice(1, ValueHash256.MemorySize));
            if (!_stagedStorageKeys.TryGetValue(addressHash, out HashSet<PbtStorageFullKey>? keys))
                _stagedStorageKeys[addressHash] = keys = [];
            keys.Add(key);
        }

        public void SetCode(in ValueHash256 codeHash, CodeInfo code) =>
            _batch.GetColumnBatch(PbtColumns.Codes).PutSpan(codeHash.Bytes, code.CodeSpan, flags);

        public void ClearStorage(in ValueHash256 addressHash)
        {
            IWriteBatch storage = _batch.GetColumnBatch(PbtColumns.Storages);
            ISortedKeyValueStore persisted = (ISortedKeyValueStore)db.GetColumnDb(PbtColumns.Storages);
            Span<byte> prefix = stackalloc byte[1 + ValueHash256.MemorySize];
            addressHash.Bytes.CopyTo(prefix[1..]);
            ReadOnlySpan<byte> zones = [Eip8297KeyDerivation.AccountZone, Eip8297KeyDerivation.StorageZone];
            foreach (byte zone in zones)
            {
                prefix[0] = zone;
                using ISortedView view = persisted.GetViewBetween(prefix, PrefixUpperBound(prefix));
                while (view.MoveNext()) storage.Set(view.CurrentKey, null, flags);
            }
            // The database view does not include earlier writes in this batch.
            if (_stagedStorageKeys.Remove(addressHash, out HashSet<PbtStorageFullKey>? keys))
                foreach (PbtStorageFullKey key in keys) storage.Set(key.Bytes, null, flags);
        }

        private static bool IsStorageKey(ReadOnlySpan<byte> key) =>
            key.Length == 34 && key[0] == Eip8297KeyDerivation.AccountZone && key[^1] is >= 64 and < 128
            || key.Length == 66 && key[0] == Eip8297KeyDerivation.StorageZone;

        public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));

            if (payload is not null)
            {
                PbtTraversalPath traversalPath = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageFullKey.MaxLength], groupKey);
                _ = new PbtNodeGroupReader(traversalPath, payload.GetSpan());
            }
            IWriteBatch groups = _batch.GetColumnBatch(NodeGroupColumn(groupKey));
            Span<byte> key = stackalloc byte[groupKey.EncodedLength];
            ReadOnlySpan<byte> storageKey = NodeGroupStorageKey(groupKey, key);
            if (payload is null) groups.Set(storageKey, null, flags);
            else groups.PutSpan(storageKey, payload.GetSpan(), flags);
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

        public void Dispose()
        {
            if (_completed) return;
            _completed = true;
            _batch.Clear();
            _batch.Dispose();
        }
    }
}
