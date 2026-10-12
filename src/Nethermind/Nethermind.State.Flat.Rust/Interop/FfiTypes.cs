// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Rust.Interop;

// Mirrors of the repr(C) types of native/crates/flatdb-ffi/src/abi.rs. FfiAbiTests checks every size against the
// library's fdb_struct_sizes.

internal static class FfiAbi
{
    /// <summary>The ABI version this adapter speaks; must equal <c>fdb_abi_version()</c>.</summary>
    public const uint Version = 1;
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
}

/// <summary>Index of each type in the sizes <c>fdb_struct_sizes</c> reports.</summary>
internal enum FfiStruct
{
    StoreCfg,
    KvDesc,
    RocksApi,
    ManagedKvVTable,
}

internal enum FdbKvKind : uint
{
    Rocks = 0,
    Managed = 1,
    Mem = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct FdbStoreCfg
{
    public uint AbiVersion;
    /// <summary>A <see cref="Nethermind.Db.FlatLayout"/>.</summary>
    public uint Layout;
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

/// <summary>Callbacks of the managed key-value backend. The native side does not implement that backend yet, so the slots stay untyped.</summary>
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
}
