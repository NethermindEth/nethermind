// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.Flat.Rust.Interop;

// Mirrors of the repr(C) types of native/crates/flatdb-ffi/src/abi.rs. FfiAbiTests checks every size against the
// library's fdb_struct_sizes.

internal static class FfiAbi
{
    /// <summary>The ABI version this adapter speaks; must equal <c>fdb_abi_version()</c>.</summary>
    public const uint Version = 2;
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
