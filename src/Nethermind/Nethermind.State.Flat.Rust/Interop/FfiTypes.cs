// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust.Interop;

// Mirrors of the repr(C) types of native/crates/flatdb-ffi/src/abi.rs. FfiAbiTests checks every size against the
// library's fdb_struct_sizes.

internal static class FfiAbi
{
    /// <summary>The ABI version this adapter speaks; must equal <c>fdb_abi_version()</c>.</summary>
    public const uint Version = 6;
}

/// <summary>Status code of a native call.</summary>
internal enum FdbStatus
{
    Ok = 0,
    StateNotRetained = 1,
    GatherTimeout = 2,
    Cancelled = 3,
    ShutDown = 4,
    Panic = 5,
    RocksDb = 6,
    Rejected = 7,
    Duplicate = 8,
    WrongBaseState = 9,
    BufferTooSmall = 10,
    Poisoned = 11,
    InvalidArgument = 12,
    NotSupported = 13,
    InvalidOperation = 14,
    Rlp = 15,
    InvalidConfiguration = 16,
    ManagedCallback = 17,
    HashMismatch = 18,
}

/// <summary>Index of each type in the sizes <c>fdb_struct_sizes</c> reports.</summary>
internal enum FfiStruct
{
    StoreCfg,
    KvDesc,
    RocksApi,
    ManagedKvVTable,
    StoreInfo,
    Stats,
    StateId,
    Account,
    MgrCfg,
    Host,
    Counters,
    Observation,
    PersistedNode,
    PersistedEvent,
    RobInfo,
    ScopeFlags,
    NodeRecord,
}

internal enum FdbKvKind : uint
{
    Rocks = 0,
    Managed = 1,
    Mem = 2,
}

/// <summary>The persistence stack of a store, as FlatWorldStateModule builds it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdbStoreCfg
{
    public uint AbiVersion;
    /// <summary>A <see cref="Nethermind.Db.FlatLayout"/>.</summary>
    public uint Layout;
    /// <summary><see cref="Nethermind.Db.IFlatDbConfig.PersistenceWriteBufferFloor"/>.</summary>
    public long WriteBufferFloor;
    /// <summary><see cref="Nethermind.Db.IFlatDbConfig.EnableCarryForwardCache"/>.</summary>
    public byte CarryForward;
    /// <summary>The most verbose <see cref="NativeLogLevel"/> the store's log sink keeps.</summary>
    public NativeLogLevel LogLevel;
}

/// <summary>A level of the native log records, as the library numbers them.</summary>
internal enum NativeLogLevel : byte
{
    Off = 0,
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}

/// <summary>The C# class whose log line a native record is, by the module that logged it (<c>log_ring::LogSource</c>).</summary>
internal enum NativeLogSource : uint
{
    /// <summary>A module without a C# counterpart.</summary>
    Other = 0,
    FlatDbManager = 1,
    PersistenceManager = 2,
    SnapshotRepository = 3,
    SnapshotCompactor = 4,
    ReadOnlySnapshotBundle = 5,
    /// <summary>The persistence class of the store's layout.</summary>
    LayoutPersistence = 6,
    DbOnTheRocks = 7,
}

/// <summary>
/// The seam counters of the native library, in the order <c>fdb_seam_counters</c> reports them: the reads of its
/// snapshot bundles by where they were served from (<c>seam::BundleRead</c>), then the calls of each export
/// (<c>calls::Export</c>).
/// </summary>
internal enum SeamCounter
{
    BundleAccountSnapshot,
    BundleAccountPersistence,
    BundleAccountPersistenceNull,
    BundleStorageSnapshot,
    BundleStorageSelfDestructed,
    BundleStoragePersistence,
    BundleStoragePersistenceNull,
    BundleStateNodeSnapshot,
    BundleStateNodePersistence,
    BundleStorageNodeSnapshot,
    BundleStorageNodePersistence,
    BundleAccountCurrent,
    BundleAccountOwn,
    BundleStorageCurrent,
    BundleStorageOwn,
    BundleStateNodeCurrent,
    BundleStateNodeOwn,
    BundleStorageNodeCurrent,
    BundleStorageNodeOwn,
    StoreCreate,
    StoreDestroy,
    StoreTakeStats,
    StoreFlush,
    StoreClear,
    StoreLogTake,
    ReaderOpen,
    ReaderRelease,
    ReaderCurrentState,
    ReaderIsPreimageMode,
    ReaderGetAccount,
    ReaderGetSlot,
    ReaderLoadStateRlp,
    ReaderLoadStorageRlp,
    ReaderGetAccountRaw,
    ReaderGetStorageRaw,
    ReaderAccountIter,
    ReaderStorageIter,
    IterNext,
    IterRelease,
    BatchOpen,
    BatchApply,
    BatchCommit,
    BatchRelease,
    MgrCreate,
    SignalExit,
    Shutdown,
    Destroy,
    DrainObservations,
    TakePersistedEvent,
    PersistedEventRelease,
    BldNew,
    BldAppend,
    BldAbsorb,
    BldRelease,
    AddSnapshot,
    QueueCompaction,
    Gather,
    HasState,
    PersistedState,
    ResetPersisted,
    AddToPersistence,
    FlushToPersistence,
    DropUnreachable,
    TakeFatalError,
    ClearBundleCache,
    RobGather,
    RobRelease,
    RobGetAccount,
    RobSelfDestructIdx,
    RobGetSlot,
    RobLoadStateRlp,
    RobLoadStorageRlp,
    ScopeOpen,
    ScopeCreate,
    ScopeRelease,
    ScopeGetAccount,
    ScopePromoteAccount,
    ScopeSetAccount,
    ScopeSelfDestructIdx,
    ScopeGetSlot,
    ScopeSetSlot,
    ScopeClearStorage,
    ScopeLoadStateRlp,
    ScopeLoadStorageRlp,
    ScopeSetStateNode,
    ScopeSetStorageNode,
    ScopePutStateNodes,
    ScopePutStorageNodes,
    ScopeCommit,
    SnapshotRelease,
    Other,
}

/// <summary>What a store resolved from its database.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdbStoreInfo
{
    public byte RlpWrapSlots;
    public byte Preimage;
}

/// <summary>The metrics a store gathered since they were last taken.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdbStats
{
    public ulong Reads;
    public long CarryForwardAccountHits;
    public long CarryForwardAccountMisses;
    public long CarryForwardSlotHits;
    public long CarryForwardSlotMisses;
    public long CarryForwardAccountWipes;
    public long CarryForwardSlotEvictions;
    public long CarryForwardAccountCount;
    public long CarryForwardSlotCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfiStateId
{
    public ulong BlockNumber;
    public fixed byte StateRoot[32];

    public FfiStateId(in StateId state)
    {
        BlockNumber = state.BlockNumber;
        state.StateRoot.Bytes.CopyTo(MemoryMarshal.CreateSpan(ref StateRoot[0], ValueHash256.MemorySize));
    }

    public readonly StateId ToStateId() =>
        new(BlockNumber, new ValueHash256(MemoryMarshal.CreateReadOnlySpan(in StateRoot[0], ValueHash256.MemorySize)));
}

/// <summary>An account; the flags mark the empty storage root and code hash, which map to the static hashes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfiAccount
{
    public const uint EmptyStorageRoot = 1;
    public const uint EmptyCodeHash = 2;

    public ulong Nonce;
    public fixed ulong Balance[4];
    public fixed byte StorageRoot[32];
    public fixed byte CodeHash[32];
    public uint Flags;

    public FfiAccount(Account account)
    {
        Nonce = account.Nonce;
        UInt256 balance = account.Balance;
        Balance[0] = balance.u0;
        Balance[1] = balance.u1;
        Balance[2] = balance.u2;
        Balance[3] = balance.u3;
        account.StorageRoot.Bytes.CopyTo(MemoryMarshal.CreateSpan(ref StorageRoot[0], Hash256.Size));
        account.CodeHash.Bytes.CopyTo(MemoryMarshal.CreateSpan(ref CodeHash[0], Hash256.Size));
        Flags = (account.StorageRoot == Keccak.EmptyTreeHash ? EmptyStorageRoot : 0)
            | (account.CodeHash == Keccak.OfAnEmptyString ? EmptyCodeHash : 0);
    }

    /// <summary>The account as <c>AccountDecoder</c> decodes it.</summary>
    public readonly Account ToAccount()
    {
        UInt256 balance = new(Balance[0], Balance[1], Balance[2], Balance[3]);
        return (Flags & (EmptyStorageRoot | EmptyCodeHash)) == (EmptyStorageRoot | EmptyCodeHash)
            ? new Account(Nonce, balance)
            : new Account(
                Nonce,
                balance,
                (Flags & EmptyStorageRoot) != 0 ? Keccak.EmptyTreeHash : new Hash256(MemoryMarshal.CreateReadOnlySpan(in StorageRoot[0], Hash256.Size)),
                (Flags & EmptyCodeHash) != 0 ? Keccak.OfAnEmptyString : new Hash256(MemoryMarshal.CreateReadOnlySpan(in CodeHash[0], Hash256.Size)));
    }
}

/// <summary>The key-value store a native store runs on; only the fields of <see cref="Kind"/> are read.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FdbKvDesc
{
    public FdbKvKind Kind;
    public uint ColumnCount;
    /// <summary>The <see cref="RocksApiTable"/>.</summary>
    public nint RocksApi;
    /// <summary><c>rocksdb_t*</c>.</summary>
    public nint RocksDb;
    /// <summary><c>rocksdb_column_family_handle_t*</c> of every column, in <see cref="FlatDbColumns"/> order.</summary>
    public nint* RocksColumnFamilies;
    public ulong ReadaheadSize;
    /// <summary><c>DbOnTheRocks.PrefixExtractorLength</c>: 0 for none, <see cref="int.MaxValue"/> for an extractor of unknown width.</summary>
    public uint PrefixExtractorLength;
    public byte VerifyChecksums;
    public byte WalSync;
    public ManagedKvVTable* Managed;
}

/// <summary>Callbacks of the managed key-value backend, implemented by <see cref="ManagedKvBridge"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ManagedKvVTable
{
    public nint Ctx;
    public nint Get;
    public nint SnapshotCreate;
    public nint SnapshotRelease;
    public nint IteratorCreate;
    public nint IteratorNext;
    public nint IteratorRelease;
    public nint Write;
    public nint Flush;
    public nint SetWriteBuffer;
}

/// <summary>
/// The settings of a native snapshot manager: the <see cref="Nethermind.Db.IFlatDbConfig"/> and
/// <see cref="Nethermind.Config.IBlocksConfig"/> values <c>FlatDbManager</c>, <c>PersistenceManager</c>,
/// <c>SnapshotCompactor</c> and <c>CompactionSchedule</c> read, with the compaction offset C# resolved.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdbMgrCfg
{
    public uint AbiVersion;
    public uint MaxInFlightCompactJob;
    public ulong CompactSize;
    public ulong PersistedSnapshotMaxCompactSize;
    public ulong CompactionOffset;
    public ulong MinReorgDepth;
    public ulong MaxReorgDepth;
    public ulong MaxInMemorySnapshotBytes;
    public ulong SecondsPerSlot;
    public double InMemorySlotFilterBitsPerKey;
    /// <summary>Histogram observations queued between drains; 0 records none.</summary>
    public uint ObservationCapacity;
    public byte InlineCompaction;
    /// <summary>Whether every persist queues its trie nodes for the <c>IsPersisted</c> replay.</summary>
    public byte PersistedNodeEvents;
    public byte DetailedMetrics;
}

/// <summary>The host callbacks of a native manager; see <see cref="RustHostCallbacks"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdbHost
{
    public nint Ctx;
    public nint FinalizedBlockNumber;
    public nint FinalizedStateRoot;
    public nint FlushDeferred;
}

/// <summary>
/// The counters of a native manager, one <c>long</c> per <see cref="Nethermind.State.Flat.Metrics"/> property, then the
/// pooled-resource gauges of its native contents, updated atomically in place.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FdbCounters
{
    /// <summary>The <see cref="ResourcePool.Usage"/> values a block's content is written under.</summary>
    public const int ContentUsages = 3;
    /// <summary>The <see cref="ResourcePool.Usage"/> values a compacted content is built under, from <c>Compact2</c>.</summary>
    public const int SortedContentUsages = 11;

    public long SnapshotCount;
    public long CompactedSnapshotCount;
    public long SnapshotMemory;
    public long CompactedSnapshotMemory;
    public long TotalSnapshotMemory;
    public long SnapshotBundleSize;
    public long SnapshotBundlePersistedSnapshotSize;
    public long SnapshotBundlePersistedSnapshotMemory;
    public long InMemorySlotFilterMemory;
    public long InMemorySlotFilterBuilds;
    public long InMemorySlotFilterBuildFailures;
    public long ObservationsDropped;
    /// <summary>Live block contents, by usage, counted as <c>ActivePooledResource</c> of <c>SnapshotContent</c>.</summary>
    public fixed long ContentActive[ContentUsages];
    public fixed long ContentCreated[ContentUsages];
    /// <summary>Live compacted contents, by usage from <c>Compact2</c>, counted as <c>ActivePooledResource</c> of <c>SortedSnapshotContent</c>.</summary>
    public fixed long SortedContentActive[SortedContentUsages];
    public fixed long SortedContentCreated[SortedContentUsages];
}

/// <summary>A histogram observation of a native manager.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdbObservation
{
    public ushort Metric;
    public ushort Label;
    public double Value;
}

/// <summary>A trie node a native persist wrote.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfiPersistedNode
{
    public const byte StorageNode = 1;

    public fixed byte AddressHash[32];
    public fixed byte Path[32];
    public fixed byte Hash[32];
    public byte PathLength;
    public byte IsStorage;

    /// <summary>The node as the <c>IsPersisted</c> replay keys it: the account path of a storage node, zero for a state node.</summary>
    public readonly (bool IsStorage, ValueHash256 Address, TreePath Path) Key =>
        (IsStorage == StorageNode,
            new ValueHash256(MemoryMarshal.CreateReadOnlySpan(in AddressHash[0], ValueHash256.MemorySize)),
            new TreePath(new ValueHash256(MemoryMarshal.CreateReadOnlySpan(in Path[0], ValueHash256.MemorySize)), PathLength));

    public readonly ValueHash256 NodeHash => new(MemoryMarshal.CreateReadOnlySpan(in Hash[0], ValueHash256.MemorySize));
}

/// <summary>The trie nodes of one native persist; <see cref="Owner"/> keeps <see cref="Nodes"/> alive until released.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FdbPersistedEvent
{
    public FfiStateId From;
    public FfiStateId To;
    public FfiPersistedNode* Nodes;
    public nuint NodeCount;
    public nint Owner;
}

/// <summary>What a native read-only bundle or scope was opened over, cached by its handle so that no call is needed to learn it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FfiRobInfo
{
    /// <summary>The persisted state the bundle reads below its snapshots.</summary>
    public FfiStateId ReaderState;
    /// <summary>The in-memory snapshots of the bundle, which number its self-destruct indexes from 0.</summary>
    public uint SnapshotCount;
    /// <summary>The snapshots a scope was seeded with, numbered after the bundle's; 0 for a read-only bundle.</summary>
    public uint OwnCount;
    /// <summary>The persisted snapshots of the bundle; 0 until the persisted-snapshot tier exists.</summary>
    public uint PersistedCount;
    public byte MayFilterSlots;
    /// <summary>Whether a snapshot of the bundle, or of a scope's seed, clears a storage.</summary>
    public byte HasSelfDestructs;
    public byte IsHistorical;
    public byte Pad;
}

/// <summary>How a native scope reads and sizes its content.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfiScopeFlags
{
    /// <summary>Slot reads that reach the scope's bundle go through its negative slot filter.</summary>
    public byte FilterInMemorySlotReads;
    /// <summary><see cref="ResourcePool.Usage.MainBlockProcessing"/>, <see cref="ResourcePool.Usage.PostMainBlockProcessing"/> or <see cref="ResourcePool.Usage.ReadOnlyProcessingEnv"/>.</summary>
    public byte Usage;
    public fixed byte Pad[2];

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is not one a block's content is written under.</exception>
    public FfiScopeFlags(bool filterInMemorySlotReads, ResourcePool.Usage usage)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)usage, (int)ResourcePool.Usage.ReadOnlyProcessingEnv, nameof(usage));
        FilterInMemorySlotReads = filterInMemorySlotReads ? (byte)1 : (byte)0;
        Usage = (byte)usage;
    }
}

/// <summary>A trie node of a batch of node writes: its path, its hash (zero for an inline node) and where its RLP lies in the batch's blob.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfiNodeRecord
{
    public fixed byte Path[32];
    public byte PathLength;
    public fixed byte Pad[3];
    /// <summary>0 for a hash-only placeholder.</summary>
    public uint RlpLength;
    public ulong RlpOffset;
    public fixed byte Hash[32];

    /// <param name="hash">The node's hash; null for an inline node.</param>
    public FfiNodeRecord(in TreePath path, Hash256? hash, int rlpLength, int rlpOffset)
    {
        path.Path.Bytes.CopyTo(MemoryMarshal.CreateSpan(ref Path[0], ValueHash256.MemorySize));
        PathLength = (byte)path.Length;
        RlpLength = (uint)rlpLength;
        RlpOffset = (ulong)rlpOffset;
        hash?.Bytes.CopyTo(MemoryMarshal.CreateSpan(ref Hash[0], ValueHash256.MemorySize));
    }
}

/// <summary>Where a read of a native bundle or scope was served from.</summary>
internal enum ReadSource : byte
{
    /// <summary>The content the scope is writing.</summary>
    Current = 1,
    /// <summary>A snapshot the scope committed, or one of its seed.</summary>
    Own = 2,
    /// <summary>An in-memory snapshot of the bundle.</summary>
    Snapshot = 3,
    Persistence = 4,
    /// <summary>Nothing: a self-destruct cut the slot off.</summary>
    SelfDestructed = 5,
}
