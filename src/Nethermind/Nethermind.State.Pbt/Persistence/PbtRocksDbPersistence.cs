// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Pbt;
using Nethermind.Evm.CodeAnalysis;
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
    // Retired stamps that epoch-23 databases still carry; the epoch alone now pins the layout and the omission.
    private static ReadOnlySpan<byte> NodeGroupKeyLayoutKey => "nodeGroupKeyLayout"u8;
    private static ReadOnlySpan<byte> PrefixlessBranchOmissionKey => "prefixlessBranchOmission"u8;
    private const int CurrentStateLength = sizeof(ulong) + 2 * ValueHash256.MemorySize;
    internal static ReadOnlySpan<byte> RootNodeGroupKey => "rootNodeGroup"u8;
    /// <remarks>
    /// Bump on any change to the persisted layout, including which prefixless branches a group omits: omission changes
    /// a group's bytes but not its hash, and the node-group caches key on the hash, so a database written with another
    /// omission can serve a group whose size no longer matches the one its ancestors recorded.
    /// </remarks>
    private const int SchemaEpoch = 23;
    /// <summary>Account groups keyed at or above this depth are top groups: the last level before the 16^8 dense band.</summary>
    internal const int AccountTopDepth = 28;
    /// <summary>Code and storage groups keyed at or above this depth are top groups: every group keyed shorter than zone and address hash.</summary>
    internal const int StemTopDepth = 260;
    private const byte ValidState = 1;

    private readonly IColumnsDb<PbtColumns> _db = EnsureSchema(db, config.ImportFromPreimageFlat || PbtMigrationConfigValidator.HasSource(config));

    internal bool IsValid => _db.GetColumnDb(PbtColumns.Metadata).Get(ValidStateKey) is not null;

    /// <summary>Whether a metadata key is a schema stamp, current or retired, so an otherwise empty database still counts as empty.</summary>
    internal static bool IsSchemaStamp(ReadOnlySpan<byte> key) =>
        key.SequenceEqual(SchemaEpochKey) || key.SequenceEqual(NodeGroupKeyLayoutKey) || key.SequenceEqual(PrefixlessBranchOmissionKey);

    private static IColumnsDb<PbtColumns> EnsureSchema(IColumnsDb<PbtColumns> db, bool allowInterruptedImport)
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
            return db;
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

        if (storedValidity is not null) return db;

        if (HasPopulatedDataColumn(db) && !allowInterruptedImport)
        {
            throw new InvalidDataException($"The epoch-{SchemaEpoch} PBT database contains an interrupted initialization. Rebuild into a new pbt database, or enable the preimage-flat import or configure the migration source to clear and retry it.");
        }
        return db;
    }

    private static bool HasPopulatedDataColumn(IColumnsDb<PbtColumns> db)
    {
        if (db.GetColumnDb(PbtColumns.Metadata).Get(RootNodeGroupKey) is not null) return true;

        foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
        {
            if (column == PbtColumns.Metadata) continue;
            using IEnumerator<KeyValuePair<byte[], byte[]>> entries = db.GetColumnDb(column).GetAll().GetEnumerator();
            if (entries.MoveNext()) return true;
        }
        return false;
    }

    public IPbtPersistence.IReader CreateReader() => new Reader(trieNodeLog.OpenView(_db));

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags)
    {
        StateId current = ReadCurrentState(_db.GetColumnDb(PbtColumns.Metadata)).State;
        if (current != from) throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, db state: {current}");
        return StartWriteBatch(to, treeRoot, flags, publishState: true);
    }

    // Staging batches may be open several at a time, which the log does not allow, so they bypass it.
    public IPbtPersistence.IWriteBatch CreateStagingWriteBatch(WriteFlags flags) =>
        StartWriteBatch(default, default, flags, publishState: false);

    private WriteBatch StartWriteBatch(StateId to, ValueHash256 root, WriteFlags flags, bool publishState)
    {
        // A group written straight to RocksDB must not be shadowed by an older version still in the log.
        if (!publishState) trieNodeLog.Drain();
        IColumnsWriteBatch<PbtColumns> batch = _db.StartWriteBatch();
        try
        {
            return new WriteBatch(_db, batch, publishState ? trieNodeLog.StartWriteBatch(batch) : NullTrieNodeLog.Instance, to, root, flags, publishState);
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
        if (value is not (null or [ValidState]))
            throw new InvalidDataException("Malformed PBT validity metadata. Rebuild or re-import into a new pbt database.");
    }

    internal static PbtColumns NodeGroupColumn<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        if (groupKey.BitDepth == 0) return PbtColumns.Metadata;
        if (groupKey.BitDepth == 4) return PbtColumns.TopNodeGroups;
        PbtColumns partition = PartitionColumn(groupKey);
        int topDepth = partition == PbtColumns.AccountNodeGroups ? AccountTopDepth : StemTopDepth;
        return groupKey.BitDepth <= topDepth ? PbtColumns.TopNodeGroups : partition;
    }

    /// <summary>The partition column of a group keyed below the shared depth-four groups, ignoring the top split.</summary>
    internal static PbtColumns PartitionColumn<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> =>
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
        private readonly IReadOnlyKeyValueStore _codeLeaves = view.Snapshot.GetColumn(PbtColumns.CodeLeaves);
        private readonly IReadOnlyKeyValueStore _accountNodeGroups = view.GetColumn(PbtColumns.AccountNodeGroups);
        private readonly IReadOnlyKeyValueStore _codeNodeGroups = view.GetColumn(PbtColumns.CodeNodeGroups);
        private readonly IReadOnlyKeyValueStore _storageNodeGroups = view.GetColumn(PbtColumns.StorageNodeGroups);
        private readonly IReadOnlyKeyValueStore _topNodeGroups = view.GetColumn(PbtColumns.TopNodeGroups);

        public StateId CurrentState => _current.State;
        public ValueHash256 CurrentRoot => _current.Root;

        public PbtAccount? GetAccount(in ValueHash256 addressHash)
        {
            ReadOnlySpan<byte> value = _accounts.GetSpan(addressHash.Bytes);
            try
            {
                return value.IsNull() ? null : PbtAccount.Decode(value);
            }
            finally
            {
                _accounts.DangerousReleaseMemory(value);
            }
        }

        public PackedSlotRun GetSlotRun(in PbtStorageTreeKey runKey)
        {
            Span<byte> persistedKey = stackalloc byte[PbtStorageTreeKey.MaxLength];
            ReadOnlySpan<byte> value = _storages.GetSpan(PbtStorageKeyLayout.Encode(runKey, persistedKey));
            try
            {
                return value.IsNull() ? SlotRun.Empty : SlotRunCodec.Decode(value);
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

        public bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value)
        {
            ReadOnlySpan<byte> stored = _codeLeaves.GetSpan(key.Bytes);
            try
            {
                if (stored.IsNull())
                {
                    value = default;
                    return false;
                }
                if (stored.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid persisted PBT code leaf.");
                value = new ValueHash256(stored);
                return true;
            }
            finally
            {
                _codeLeaves.DangerousReleaseMemory(stored);
            }
        }

        public IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts()
        {
            ISortedKeyValueStore accounts = (ISortedKeyValueStore)_accounts;
            Span<byte> upper = stackalloc byte[ValueHash256.MemorySize + 1];
            upper.Fill(byte.MaxValue);
            using ISortedView view = accounts.GetViewBetween([], upper);
            while (view.MoveNext())
                yield return new(new ValueHash256(view.CurrentKey), PbtAccount.Decode(view.CurrentValue));
        }

        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
        {
            PbtColumns column = NodeGroupColumn(groupKey);
            Span<byte> key = stackalloc byte[PbtNodeGroupKey.MaxLength];
            MemoryManager<byte>? owned = GetNodeGroupColumn(column).GetOwnedMemory(NodeGroupStorageKey(column, groupKey, key));
            // The trie node cache leases this memory as-is, so a cached group keeps its RocksDB block-cache block pinned
            // until eviction. That is acceptable: the block cache is budgeted for it and it saves a copy per cached read.
            if (owned is null) return null;
            PbtNodeGroupCodec.DebugValidateNodes(groupKey, owned.Memory.Span);
            return RefCountingMemory.OwningRocksDb(owned);
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
        public void SetAccount(in ValueHash256 addressHash, PbtAccount? account)
        {
            IWriteBatch accounts = batch.GetColumnBatch(PbtColumns.Accounts);
            if (account is not { } value) accounts.Set(addressHash.Bytes, null, flags);
            else
            {
                Span<byte> encoded = stackalloc byte[PbtAccount.MaxEncodedLength];
                value.Encode(encoded);
                accounts.PutSpan(addressHash.Bytes, encoded[..value.EncodedLength], flags);
            }
        }

        public void SetSlotRun(in PbtStorageTreeKey runKey, PackedSlotRun run)
        {
            if (!IsStorageKey(runKey.Bytes)) throw new ArgumentException("A complete storage run key is required.", nameof(runKey));
            IWriteBatch storage = batch.GetColumnBatch(PbtColumns.Storages);
            Span<byte> persistedKey = stackalloc byte[PbtStorageTreeKey.MaxLength];
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
            batch.GetColumnBatch(PbtColumns.Codes).PutSpan(codeHash.Bytes, code.CodeSpan, flags);

        public void SetCodeLeaf(in PbtPath key, in ValueHash256 value) =>
            batch.GetColumnBatch(PbtColumns.CodeLeaves).PutSpan(key.Bytes, value.Bytes, flags);

        private static bool IsStorageKey(ReadOnlySpan<byte> key) =>
            key.Length == 34 && key[0] == Eip8297KeyDerivation.AccountZone && key[^1] is >= 64 and < 128
            || key.Length == 66 && key[0] == Eip8297KeyDerivation.StorageZone;

        public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            if (payload is not null) PbtNodeGroupCodec.DebugValidateNodes(groupKey, payload.GetSpan());
            PbtColumns column = NodeGroupColumn(groupKey);
            IWriteBatch groups = logBatch.Wrap(column, batch.GetColumnBatch(column));
            Span<byte> key = stackalloc byte[PbtNodeGroupKey.MaxLength];
            ReadOnlySpan<byte> storageKey = NodeGroupStorageKey(column, groupKey, key);
            if (payload is null) groups.Set(storageKey, null, flags);
            else groups.PutSpan(storageKey, payload.GetSpan(), flags);
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
                _completed = true;
                batch.Clear();
                batch.Dispose();
                logBatch.Dispose();
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
