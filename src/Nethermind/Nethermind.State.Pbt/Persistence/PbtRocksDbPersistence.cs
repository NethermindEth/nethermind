// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Pbt;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;

namespace Nethermind.State.Pbt.Persistence;

/// <summary><see cref="IPbtPersistence"/> backed by canonical PBT columns.</summary>
public class PbtRocksDbPersistence(
    IColumnsDb<PbtColumns> db,
    IPbtConfig config,
    ITrieNodeLog trieNodeLog) : IPbtPersistence
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private static ReadOnlySpan<byte> ValidStateKey => "validState"u8;
    private const int CurrentStateLength = sizeof(ulong) + 2 * ValueHash256.MemorySize;
    public static ReadOnlySpan<byte> RootNodeGroupKey => "rootNodeGroup"u8;
    /// <remarks>
    /// Bump on any change to the persisted layout, including which prefixless branches a group omits: omission changes
    /// a group's bytes but not its hash, and the node-group caches key on the hash, so a database written with another
    /// omission can serve a group whose size no longer matches the one its ancestors recorded.
    /// </remarks>
    private const int SchemaEpoch = 24;
    private const byte ValidState = 1;

    private readonly IColumnsDb<PbtColumns> _db = EnsureSchema(db, config.ImportFromPreimageFlat || PbtMigrationConfigValidator.HasSource(config));

    public bool IsValid => _db.GetColumnDb(PbtColumns.Metadata).KeyExists(ValidStateKey);

    /// <summary>Whether a metadata key is the schema stamp, so an otherwise empty database still counts as empty.</summary>
    public static bool IsSchemaStamp(ReadOnlySpan<byte> key) => key.SequenceEqual(SchemaEpochKey);

    private static IColumnsDb<PbtColumns> EnsureSchema(IColumnsDb<PbtColumns> db, bool allowInterruptedImport)
    {
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        int epoch = 0;
        bool hasEpoch = metadata.TryGet(SchemaEpochKey, static (value, epoch) => epoch.Value = value.Length == sizeof(int)
            ? BinaryPrimitives.ReadInt32BigEndian(value)
            : throw new InvalidDataException("Malformed PBT schema epoch. Rebuild or re-import into a new pbt database."), new PbtReadResult<int>(ref epoch));
        bool hasCurrentState = metadata.TryGet(CurrentStateKey, static (value, _) => DecodeCurrentState(value), 0);
        bool hasValidity = metadata.TryGet(ValidStateKey, static (value, _) => ValidateValidity(value), 0);

        if (!hasEpoch)
        {
            if (hasCurrentState || hasValidity || HasPopulatedDataColumn(db))
            {
                throw new InvalidDataException($"The populated pbt database has no schema epoch {SchemaEpoch} stamp. Rebuild or re-import into a new pbt database.");
            }

            Span<byte> value = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(value, SchemaEpoch);
            metadata.PutSpan(SchemaEpochKey, value, WriteFlags.None);
            return db;
        }

        if (epoch != SchemaEpoch)
        {
            throw new InvalidDataException($"The pbt database uses schema epoch {epoch}, but this build reads epoch {SchemaEpoch}. Rebuild or re-import into a new pbt database.");
        }

        if (hasCurrentState != hasValidity)
        {
            throw new InvalidDataException("The PBT validity marker and current-state metadata are inconsistent. Rebuild or re-import into a new pbt database.");
        }

        if (hasValidity) return db;

        if (HasPopulatedDataColumn(db) && !allowInterruptedImport)
        {
            throw new InvalidDataException($"The epoch-{SchemaEpoch} PBT database contains an interrupted initialization. Rebuild into a new pbt database, or enable the preimage-flat import or configure the migration source to clear and retry it.");
        }
        return db;
    }

    private static bool HasPopulatedDataColumn(IColumnsDb<PbtColumns> db)
    {
        if (db.GetColumnDb(PbtColumns.Metadata).KeyExists(RootNodeGroupKey)) return true;

        foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
        {
            if (column == PbtColumns.Metadata) continue;
            if (((ISortedKeyValueStore)db.GetColumnDb(column)).FirstKey is not null) return true;
        }
        return false;
    }

    public IPbtPersistence.IReader CreateReader() => new Reader(trieNodeLog.OpenView(_db));

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        StateId current = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata)).State;
        if (current != from) throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {current}");
        return StartWriteBatch(to, treeRoot, publishState: true);
    }

    // Staging batches may be open several at a time, which the log does not allow, so they bypass it.
    public IPbtPersistence.IWriteBatch CreateStagingWriteBatch() =>
        StartWriteBatch(default, default, publishState: false);

    private WriteBatch StartWriteBatch(StateId to, ValueHash256 root, bool publishState)
    {
        // A group written straight to RocksDB must not be shadowed by an older version still in the log.
        if (!publishState) trieNodeLog.Drain();
        IColumnsWriteBatch<PbtColumns> batch = _db.StartWriteBatch();
        try
        {
            return new WriteBatch(_db, batch, publishState ? trieNodeLog.StartWriteBatch(batch) : NullTrieNodeLog.Instance, to, root, publishState ? WriteFlags.None : WriteFlags.DisableWAL, publishState);
        }
        catch
        {
            batch.Clear();
            batch.Dispose();
            throw;
        }
    }

    public void Flush()
    {
        trieNodeLog.Drain();
        _db.Flush();
    }

    private static (StateId State, ValueHash256 Root) ReadCurrentState(IReadOnlyKeyValueStore metadata)
    {
        (StateId State, ValueHash256 Root) current = (StateId.PreGenesis, default);
        metadata.TryGet(CurrentStateKey, static (value, current) => current.Value = DecodeCurrentState(value), new PbtReadResult<(StateId, ValueHash256)>(ref current));
        return current;
    }

    private static (StateId State, ValueHash256 Root) DecodeCurrentState(ReadOnlySpan<byte> value)
    {
        if (value.Length != CurrentStateLength)
            throw new InvalidDataException("Malformed PBT current-state metadata. Rebuild or re-import into a new pbt database.");
        return (new StateId(BinaryPrimitives.ReadUInt64BigEndian(value), new ValueHash256(value.Slice(sizeof(ulong), ValueHash256.MemorySize))),
            new ValueHash256(value[(sizeof(ulong) + ValueHash256.MemorySize)..]));
    }

    private static void ValidateValidity(ReadOnlySpan<byte> value)
    {
        if (value is not [ValidState])
            throw new InvalidDataException("Malformed PBT validity metadata. Rebuild or re-import into a new pbt database.");
    }

    public static PbtColumns NodeGroupColumn<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        if (groupKey.BitDepth == 0) return PbtColumns.Metadata;
        return PbtNodeGroupLayout.IsTopGroup(groupKey) ? PbtColumns.TopNodeGroups : PartitionColumn(groupKey);
    }

    /// <summary>The partition column of a group keyed below the groups the zones share, ignoring the top split.</summary>
    public static PbtColumns PartitionColumn<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> =>
        PbtPartitions.PartitionOfPath(groupKey) switch
        {
            PbtPartition.Storage => PbtColumns.StorageNodeGroups,
            PbtPartition.Code => PbtColumns.CodeNodeGroups,
            _ => PbtColumns.AccountNodeGroups,
        };

    private static ReadOnlySpan<byte> NodeGroupStorageKey<TPath>(PbtColumns column, TPath groupKey, Span<byte> destination) where TPath : struct, IPbtNodePath<TPath> =>
        column == PbtColumns.Metadata ? RootNodeGroupKey : PbtNodeGroupKey.Encode(groupKey, destination);

    private sealed class Reader(ITrieNodeLog.IView view) : IPbtPersistence.IReader
    {
        private readonly (StateId State, ValueHash256 Root) _current = ReadCurrentState(view.Snapshot.GetColumn(PbtColumns.Metadata));
        private readonly IReadOnlyKeyValueStore _metadata = view.Snapshot.GetColumn(PbtColumns.Metadata);
        private readonly IReadOnlyKeyValueStore _accounts = view.Snapshot.GetColumn(PbtColumns.Accounts);
        private readonly IReadOnlyKeyValueStore _storages = view.Snapshot.GetColumn(PbtColumns.Storages);
        private readonly IReadOnlyKeyValueStore _codes = view.Snapshot.GetColumn(PbtColumns.Codes);
        private readonly IReadOnlyKeyValueStore _accountNodeGroups = view.GetColumn(PbtColumns.AccountNodeGroups);
        private readonly IReadOnlyKeyValueStore _codeNodeGroups = view.GetColumn(PbtColumns.CodeNodeGroups);
        private readonly IReadOnlyKeyValueStore _storageNodeGroups = view.GetColumn(PbtColumns.StorageNodeGroups);
        private readonly IReadOnlyKeyValueStore _topNodeGroups = view.GetColumn(PbtColumns.TopNodeGroups);

        public StateId CurrentState => _current.State;
        public ValueHash256 CurrentRoot => _current.Root;

        public PbtAccount? GetAccount(in ValueHash256 addressHash)
        {
            PbtAccount? account = null;
            _accounts.TryGet(addressHash.Bytes, static (value, account) => account.Value = PbtAccount.Decode(value), new PbtReadResult<PbtAccount?>(ref account));
            return account;
        }

        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey>
        {
            Span<byte> persistedKey = stackalloc byte[TKey.Capacity];
            PackedSlotRun run = SlotRun.Empty;
            _storages.TryGet(PbtStorageKeyLayout.Encode(runKey, persistedKey), static (value, run) => run.Value = SlotRun.Decode(value), new PbtReadResult<PackedSlotRun>(ref run));
            return run;
        }

        public CodeInfo? GetCode(in ValueHash256 codeHash)
        {
            CodeInfo? code = null;
            _codes.TryGet(codeHash.Bytes, static (value, code) => code.Value = new CodeInfo(value.ToArray()), new PbtReadResult<CodeInfo?>(ref code));
            return code;
        }

        public bool TryLoadNodeGroup<TPath, TArg>(in TPath groupKey, ReadOnlySpanAction<byte, TArg> action, TArg arg)
            where TPath : struct, IPbtNodePath<TPath> where TArg : allows ref struct
        {
            PbtColumns column = NodeGroupColumn(groupKey);
            // A group was validated as it was written.
            return GetNodeGroupColumn(column).TryGet(NodeGroupStorageKey(column, groupKey, stackalloc byte[PbtNodeGroupKey.MaxLength]), action, arg);
        }

        private IReadOnlyKeyValueStore GetNodeGroupColumn(PbtColumns column) => column switch
        {
            PbtColumns.Metadata => _metadata,
            PbtColumns.AccountNodeGroups => _accountNodeGroups,
            PbtColumns.CodeNodeGroups => _codeNodeGroups,
            PbtColumns.StorageNodeGroups => _storageNodeGroups,
            PbtColumns.TopNodeGroups => _topNodeGroups,
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };

        public void Dispose() => view.Dispose();
    }

    private sealed class WriteBatch(
        IColumnsDb<PbtColumns> db,
        IColumnsWriteBatch<PbtColumns> batch,
        ITrieNodeLog.IWriteBatch logBatch,
        StateId to,
        ValueHash256 root,
        WriteFlags flags,
        bool publishState) : IPbtPersistence.IWriteBatch
    {
        private readonly IWriteBatch?[] _columnBatches = new IWriteBatch?[Enum.GetValues<PbtColumns>().Length];

        private IWriteBatch ColumnBatch(PbtColumns column) =>
            _columnBatches[(int)column] ??= logBatch.Wrap(column, batch.GetColumnBatch(column));

        public void SetAccount(in ValueHash256 addressHash, PbtAccount? account)
        {
            IWriteBatch accounts = ColumnBatch(PbtColumns.Accounts);
            if (account is not { } value) accounts.Set(addressHash.Bytes, null, flags);
            else
            {
                Span<byte> encoded = stackalloc byte[PbtAccount.MaxEncodedLength];
                value.Encode(encoded);
                accounts.PutSpan(addressHash.Bytes, encoded[..value.EncodedLength], flags);
            }
        }

        public void SetSlotRun<TKey>(in TKey runKey, PackedSlotRun run) where TKey : struct, IPbtKey<TKey>
        {
            if (!IsStorageKey(runKey.Bytes)) throw new ArgumentException("A complete storage run key is required.", nameof(runKey));
            IWriteBatch storage = ColumnBatch(PbtColumns.Storages);
            Span<byte> persistedKey = stackalloc byte[TKey.Capacity];
            ReadOnlySpan<byte> encodedKey = PbtStorageKeyLayout.Encode(runKey, persistedKey);
            if (run.Count == 0) storage.Set(encodedKey, null, flags);
            else
            {
                Span<byte> encoded = stackalloc byte[run.EncodedLength];
                run.Encode(encoded);
                storage.PutSpan(encodedKey, encoded, flags);
            }
        }

        public void SetCode(in ValueHash256 codeHash, CodeInfo code) =>
            ColumnBatch(PbtColumns.Codes).PutSpan(codeHash.Bytes, code.CodeSpan, flags);

        private static bool IsStorageKey(ReadOnlySpan<byte> key) =>
            key.Length == PbtPath.KeyLength && key[0] == Eip8297KeyDerivation.AccountZone
                && key[^1] is >= PbtKeyDerivation.HeaderStorageOffset and < PbtKeyDerivation.HeaderStorageOffset + PbtKeyDerivation.HeaderStorageSlots
            || key.Length == PbtStoragePath.KeyLength && key[0] == Eip8297KeyDerivation.StorageZone;

        public void SetNodeGroup<TPath>(TPath groupKey, scoped ReadOnlySpan<byte> payload) where TPath : struct, IPbtNodePath<TPath>
        {
            if (!payload.IsEmpty) PbtNodeGroupCodec.DebugValidateNodes(groupKey, payload);
            PbtColumns column = NodeGroupColumn(groupKey);
            IWriteBatch groups = ColumnBatch(column);
            Span<byte> key = stackalloc byte[PbtNodeGroupKey.MaxLength];
            ReadOnlySpan<byte> storageKey = NodeGroupStorageKey(column, groupKey, key);
            if (payload.IsEmpty) groups.Set(storageKey, null, flags);
            else groups.PutSpan(storageKey, payload, flags);
        }

        private bool _completed;

        public void Commit()
        {
            if (_completed) return;
            try
            {
                // The log is made durable and its version put into this batch's metadata before RocksDB commits, and
                // confirmed to the log only once RocksDB has committed and flushed its WAL.
                logBatch.Commit();
                if (publishState)
                {
                    Span<byte> value = stackalloc byte[CurrentStateLength];
                    BinaryPrimitives.WriteUInt64BigEndian(value, to.BlockNumber);
                    to.StateRoot.Bytes.CopyTo(value[sizeof(ulong)..]);
                    root.Bytes.CopyTo(value[(sizeof(ulong) + ValueHash256.MemorySize)..]);
                    IWriteBatch metadata = batch.GetColumnBatch(PbtColumns.Metadata);
                    metadata.PutSpan(CurrentStateKey, value, flags);
                    metadata.PutSpan(ValidStateKey, [ValidState], flags);
                }
            }
            catch
            {
                Dispose();
                throw;
            }

            _completed = true;
            try
            {
                batch.Dispose();
                if (!flags.HasFlag(WriteFlags.DisableWAL)) db.Flush(onlyWal: true);
                logBatch.Confirm();
            }
            finally
            {
                logBatch.Dispose();
            }
        }

        public void Dispose()
        {
            if (_completed) return;
            _completed = true;
            batch.Clear();
            batch.Dispose();
            logBatch.Dispose();
        }
    }
}
