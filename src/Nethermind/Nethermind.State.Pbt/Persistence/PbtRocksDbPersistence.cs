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
    private static ReadOnlySpan<byte> NodeGroupKeyLayoutKey => "nodeGroupKeyLayout"u8;
    private const int CurrentStateLength = sizeof(ulong) + 2 * ValueHash256.MemorySize;
    internal static ReadOnlySpan<byte> RootNodeGroupKey => "rootNodeGroup"u8;
    private const int SchemaEpoch = 16;
    private const byte ValidState = 1;

    private readonly IColumnsDb<PbtColumns> _db = Initialize(db, config.NodeGroupKeyLayout, config.ImportFromPreimageFlat);
    private readonly PbtNodeGroupKeyLayout _layout = config.NodeGroupKeyLayout;

    internal bool IsValid => _db.GetColumnDb(PbtColumns.Metadata).Get(ValidStateKey) is not null;

    /// <summary>Whether a metadata key is one written when the schema is stamped, so an otherwise empty database still counts as empty.</summary>
    internal static bool IsSchemaStamp(ReadOnlySpan<byte> key) =>
        key.SequenceEqual(SchemaEpochKey) || key.SequenceEqual(NodeGroupKeyLayoutKey);

    private static IColumnsDb<PbtColumns> Initialize(IColumnsDb<PbtColumns> db, PbtNodeGroupKeyLayout layout, bool allowInterruptedImport)
    {
        EnsureSchema(db, layout, allowInterruptedImport);
        return db;
    }

    private static void EnsureSchema(IColumnsDb<PbtColumns> db, PbtNodeGroupKeyLayout layout, bool allowInterruptedImport)
    {
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        byte[]? storedEpoch = metadata.Get(SchemaEpochKey);
        byte[]? storedCurrentState = metadata.Get(CurrentStateKey);
        byte[]? storedValidity = metadata.Get(ValidStateKey);
        byte[]? storedLayout = metadata.Get(NodeGroupKeyLayoutKey);

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
            metadata.PutSpan(NodeGroupKeyLayoutKey, [(byte)layout], WriteFlags.None);
            return;
        }

        int epoch = BinaryPrimitives.ReadInt32BigEndian(storedEpoch);
        if (epoch != SchemaEpoch)
        {
            throw new InvalidDataException($"The pbt database uses schema epoch {epoch}, but this build reads epoch {SchemaEpoch}. Rebuild or re-import into a new pbt database.");
        }
        ValidateLayout(storedLayout, layout);

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

        PbtColumns[] columns = [PbtColumns.FullLeaves,
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

    public IPbtPersistence.IReader CreateReader() => new Reader(_db.CreateSnapshot(), _layout);

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags)
    {
        StateId current = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata)).State;
        if (current != from) throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {current}");
        return new WriteBatch(_db, _layout, to, treeRoot, flags, publishState: true);
    }

    public IPbtPersistence.IWriteBatch CreateStagingWriteBatch(WriteFlags flags) =>
        new WriteBatch(_db, _layout, default, default, flags, publishState: false);

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

    /// <remarks>Databases stamped before the layout became configurable carry no stamp and use the padded layout.</remarks>
    private static void ValidateLayout(byte[]? value, PbtNodeGroupKeyLayout configured)
    {
        if (value is not null && value is not [(byte)PbtNodeGroupKeyLayout.Padded or (byte)PbtNodeGroupKeyLayout.Variable])
            throw new InvalidDataException("Malformed PBT node-group key layout stamp. Rebuild or re-import into a new pbt database.");
        PbtNodeGroupKeyLayout stored = value is null ? PbtNodeGroupKeyLayout.Padded : (PbtNodeGroupKeyLayout)value[0];
        if (stored != configured)
            throw new InvalidDataException($"The pbt database uses node-group key layout {stored}, but {nameof(IPbtConfig.NodeGroupKeyLayout)} is {configured}. Match the setting, or rebuild or re-import into a new pbt database.");
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

    private static ReadOnlySpan<byte> NodeGroupStorageKey<TPath>(PbtNodeGroupKeyLayout layout, PbtColumns column, TPath groupKey, Span<byte> destination) where TPath : struct, IPbtNodePath<TPath> =>
        column == PbtColumns.Metadata ? RootNodeGroupKey : PbtNodeGroupKey.Encode(layout, column, groupKey, destination);

    private static ReadOnlySpan<byte> PrefixUpperBound(ReadOnlySpan<byte> prefix, Span<byte> destination)
    {
        Span<byte> upper = destination[..prefix.Length];
        prefix.CopyTo(upper);
        for (int i = upper.Length - 1; i >= 0; i--)
        {
            if (++upper[i] != 0) return upper[..(i + 1)];
        }
        Span<byte> maximum = destination[..(PbtStorageTreeKey.MaxLength + 1)];
        maximum.Fill(byte.MaxValue);
        return maximum;
    }

    /// <summary>Decodes a persisted slot value: the stripped big-endian bytes, RLP-wrapped as in the flat Storage column.</summary>
    internal static EvmWord DecodeSlot(ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> stripped = new RlpReader(value).DecodeByteArraySpan();
        if (stripped.Length > ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT storage value length.");
        return EvmWordSlot.FromStripped(stripped);
    }

    private sealed class Reader(IColumnDbSnapshot<PbtColumns> snapshot, PbtNodeGroupKeyLayout layout) : IPbtPersistence.IReader
    {
        private readonly (StateId State, ValueHash256 Root) _current = ReadCurrentState(snapshot.GetColumn(PbtColumns.Metadata));
        private readonly IReadOnlyKeyValueStore _metadata = snapshot.GetColumn(PbtColumns.Metadata);
        private readonly IReadOnlyKeyValueStore _accounts = snapshot.GetColumn(PbtColumns.Accounts);
        private readonly IReadOnlyKeyValueStore _storages = snapshot.GetColumn(PbtColumns.Storages);
        private readonly IReadOnlyKeyValueStore _codes = snapshot.GetColumn(PbtColumns.Codes);
        private readonly IReadOnlyKeyValueStore _accountNodeGroups = snapshot.GetColumn(PbtColumns.AccountNodeGroups);
        private readonly IReadOnlyKeyValueStore _codeNodeGroups = snapshot.GetColumn(PbtColumns.CodeNodeGroups);
        private readonly IReadOnlyKeyValueStore _storageNodeGroups = snapshot.GetColumn(PbtColumns.StorageNodeGroups);

        public StateId CurrentState => _current.State;
        public ValueHash256 CurrentRoot => _current.Root;

        public Account? GetAccount(in ValueHash256 addressHash)
        {
            ReadOnlySpan<byte> value = _accounts.GetSpan(addressHash.Bytes);
            try
            {
                return value.IsNull() ? null : DecodeAccount(value);
            }
            finally
            {
                _accounts.DangerousReleaseMemory(value);
            }
        }

        public EvmWord GetSlot(in PbtStorageTreeKey key)
        {
            Span<byte> persistedKey = stackalloc byte[PbtStorageTreeKey.MaxLength];
            ReadOnlySpan<byte> value = _storages.GetSpan(PbtStorageKeyLayout.Encode(key, persistedKey));
            try
            {
                return value.IsNull() ? default : DecodeSlot(value);
            }
            finally
            {
                _storages.DangerousReleaseMemory(value);
            }
        }

        public CodeInfo? GetCode(in ValueHash256 codeHash)
        {
            ReadOnlySpan<byte> value = _codes.GetSpan(codeHash.Bytes);
            try
            {
                return value.IsNull() ? null : new CodeInfo(value.ToArray());
            }
            finally
            {
                _codes.DangerousReleaseMemory(value);
            }
        }

        public IPbtIterator<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() =>
            new PbtIterator<KeyValuePair<ValueHash256, Account>>(EnumerateAccountsCore());

        private IEnumerator<KeyValuePair<ValueHash256, Account>> EnumerateAccountsCore()
        {
            ISortedKeyValueStore accounts = (ISortedKeyValueStore)_accounts;
            Span<byte> upper = stackalloc byte[ValueHash256.MemorySize + 1];
            upper.Fill(byte.MaxValue);
            using ISortedView view = accounts.GetViewBetween([], upper);
            while (view.MoveNext())
                yield return new(new ValueHash256(view.CurrentKey), DecodeAccount(view.CurrentValue));
        }

        public IPbtIterator<KeyValuePair<PbtStorageTreeKey, EvmWord>> EnumerateStorage(ValueHash256? addressHash = null) =>
            new PbtIterator<KeyValuePair<PbtStorageTreeKey, EvmWord>>(EnumerateStorageCore(addressHash));

        private IEnumerator<KeyValuePair<PbtStorageTreeKey, EvmWord>> EnumerateStorageCore(ValueHash256? addressHash)
        {
            ISortedKeyValueStore storage = (ISortedKeyValueStore)_storages;
            Span<byte> upper = stackalloc byte[PbtStorageTreeKey.MaxLength + 1];
            ReadOnlySpan<byte> prefix = addressHash is null ? [] : addressHash.Value.Bytes;
            using ISortedView view = storage.GetViewBetween(prefix, PrefixUpperBound(prefix, upper));
            while (view.MoveNext())
                yield return new(PbtStorageKeyLayout.Decode(view.CurrentKey), DecodeSlot(view.CurrentValue));
        }

        private static Account DecodeAccount(ReadOnlySpan<byte> value)
        {
            RlpReader reader = new(value);
            return AccountDecoder.Slim.Decode(ref reader) ?? throw new InvalidDataException("Invalid persisted PBT account.");
        }

        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
        {
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));

            PbtColumns column = NodeGroupColumn(groupKey);
            Span<byte> key = stackalloc byte[PbtNodeGroupKey.MaxLength];
            MemoryManager<byte>? owned = GetNodeGroupColumn(column).GetOwnedMemory(NodeGroupStorageKey(layout, column, groupKey, key));
            return owned is null ? null : RefCountingMemory.OwningRocksDb(owned);
        }

        public IPbtIterator<PbtStorageNodePath> EnumerateNodeGroupKeys() =>
            new PbtIterator<PbtStorageNodePath>(EnumerateNodeGroupKeysCore());

        private IEnumerator<PbtStorageNodePath> EnumerateNodeGroupKeysCore()
        {
            if (_metadata.Get(RootNodeGroupKey) is not null)
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
                yield return PbtNodeGroupKey.Decode(layout, next.CurrentKey);
                if (ReferenceEquals(next, accounts)) hasAccount = accounts.MoveNext();
                else if (ReferenceEquals(next, codes)) hasCode = codes.MoveNext();
                else hasStorage = storage.MoveNext();
            }
        }

        private IReadOnlyKeyValueStore GetNodeGroupColumn(PbtColumns column) => column switch
        {
            PbtColumns.Metadata => _metadata,
            PbtColumns.AccountNodeGroups => _accountNodeGroups,
            PbtColumns.CodeNodeGroups => _codeNodeGroups,
            PbtColumns.StorageNodeGroups => _storageNodeGroups,
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };

        private ISortedView OpenGroups(PbtColumns column)
        {
            ISortedKeyValueStore groups = (ISortedKeyValueStore)GetNodeGroupColumn(column);
            Span<byte> upper = stackalloc byte[PbtNodeGroupKey.MaxLength + 1];
            upper.Fill(byte.MaxValue);
            return groups.GetViewBetween([], upper);
        }

        public void Dispose() => snapshot.Dispose();
    }

    private sealed class WriteBatch(
        IColumnsDb<PbtColumns> db,
        PbtNodeGroupKeyLayout layout,
        StateId to,
        ValueHash256 root,
        WriteFlags flags,
        bool publishState) : IPbtPersistence.IWriteBatch
    {
        private readonly IColumnsWriteBatch<PbtColumns> _batch = db.StartWriteBatch();

        private readonly Dictionary<ValueHash256, HashSet<PbtStorageTreeKey>> _stagedStorageKeys = [];

        public void SetAccount(in ValueHash256 addressHash, Account? account)
        {
            IWriteBatch accounts = _batch.GetColumnBatch(PbtColumns.Accounts);
            if (account is null) accounts.Set(addressHash.Bytes, null, flags);
            else
            {
                using ArrayPoolSpan<byte> encoded = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
                accounts.PutSpan(addressHash.Bytes, encoded, flags);
            }
        }

        public void SetSlot(in PbtStorageTreeKey key, in EvmWord value)
        {
            if (!IsStorageKey(key.Bytes)) throw new ArgumentException("A complete storage key is required.", nameof(key));
            IWriteBatch storage = _batch.GetColumnBatch(PbtColumns.Storages);
            Span<byte> persistedKey = stackalloc byte[PbtStorageTreeKey.MaxLength];
            ReadOnlySpan<byte> encodedKey = PbtStorageKeyLayout.Encode(key, persistedKey);
            if (EvmWordSlot.IsZero(value)) storage.Set(encodedKey, null, flags);
            else
            {
                Span<byte> encoded = stackalloc byte[ValueHash256.MemorySize + 1];
                int length = Rlp.Encode(EvmWordSlot.AsReadOnlySpan(in value).WithoutLeadingZeros(), encoded);
                storage.PutSpan(encodedKey, encoded[..length], flags);
            }
            ValueHash256 addressHash = new(key.Bytes.Slice(1, ValueHash256.MemorySize));
            if (!_stagedStorageKeys.TryGetValue(addressHash, out HashSet<PbtStorageTreeKey>? keys))
                _stagedStorageKeys[addressHash] = keys = [];
            keys.Add(key);
        }

        public void SetCode(in ValueHash256 codeHash, CodeInfo code) =>
            _batch.GetColumnBatch(PbtColumns.Codes).PutSpan(codeHash.Bytes, code.CodeSpan, flags);

        public void ClearStorage(in ValueHash256 addressHash)
        {
            IWriteBatch storage = _batch.GetColumnBatch(PbtColumns.Storages);
            ISortedKeyValueStore persisted = (ISortedKeyValueStore)db.GetColumnDb(PbtColumns.Storages);
            Span<byte> upper = stackalloc byte[PbtStorageTreeKey.MaxLength + 1];
            using (ISortedView view = persisted.GetViewBetween(addressHash.Bytes, PrefixUpperBound(addressHash.Bytes, upper)))
            {
                while (view.MoveNext()) storage.Set(view.CurrentKey, null, flags);
            }
            // The database view does not include earlier writes in this batch.
            if (_stagedStorageKeys.Remove(addressHash, out HashSet<PbtStorageTreeKey>? keys))
            {
                Span<byte> persistedKey = stackalloc byte[PbtStorageTreeKey.MaxLength];
                foreach (PbtStorageTreeKey key in keys) storage.Set(PbtStorageKeyLayout.Encode(key, persistedKey), null, flags);
            }
        }

        private static bool IsStorageKey(ReadOnlySpan<byte> key) =>
            key.Length == 34 && key[0] == Eip8297KeyDerivation.AccountZone && key[^1] is >= 64 and < 128
            || key.Length == 66 && key[0] == Eip8297KeyDerivation.StorageZone;

        public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
                throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));

            if (payload is not null) PbtNodeGroupCodec.ValidateFraming(groupKey.BitDepth, payload.GetSpan());
            PbtColumns column = NodeGroupColumn(groupKey);
            IWriteBatch groups = _batch.GetColumnBatch(column);
            Span<byte> key = stackalloc byte[PbtNodeGroupKey.MaxLength];
            ReadOnlySpan<byte> storageKey = NodeGroupStorageKey(layout, column, groupKey, key);
            if (payload is null) groups.Set(storageKey, null, flags);
            else groups.PutSpan(storageKey, payload.GetSpan(), flags);
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
