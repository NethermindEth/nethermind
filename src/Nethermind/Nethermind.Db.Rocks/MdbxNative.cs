// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.Db.Rocks;

internal static class MdbxNative
{
    private const string LibraryName = "mdbx";

    internal const int Success = 0;
    internal const int ResultTrue = -1;
    internal const int KeyExists = -30799;
    internal const int NotFound = -30798;
    internal const int KeyMismatch = -30418;

    internal const uint EnvNoSubDir = 0x4000;
    internal const uint EnvSafeNoSync = 0x10000;
    internal const uint EnvNoMetaSync = 0x40000;
    internal const uint EnvWriteMap = 0x80000;
    internal const uint EnvNoStickyThreads = 0x200000;
    internal const uint EnvNoReadAhead = 0x800000;
    internal const uint EnvNoMemInit = 0x1000000;
    internal const uint EnvCoalesce = 0x2000000;
    internal const uint EnvLifoReclaim = 0x4000000;

    internal const uint ReadOnly = 0x20000;
    internal const uint Create = 0x40000;
    internal const uint TxnNoMetaSync = EnvNoMetaSync;
    internal const uint TxnNoSync = EnvSafeNoSync;

    internal const uint PutUpsert = 0;
    internal const uint PutAppend = 0x20000;

    private const uint EnvOptionMaxDbs = 0;
    private const uint EnvOptionMaxReaders = 1;
    private const uint EnvOptionSyncBytes = 2;
    private const uint EnvOptionSyncPeriod = 3;
    private const uint EnvOptionRpAugmentLimit = 4;
    private const uint EnvOptionDpReserveLimit = 6;
    private const uint EnvOptionTxnDpLimit = 7;
    private const uint EnvOptionTxnDpInitial = 8;

    static MdbxNative() => NativeLibrary.SetDllImportResolver(typeof(MdbxNative).Assembly, ResolveLibrary);

    internal static void EnsureSupported()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The MDBX backend is supported only on linux-x64.");
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        string? configuredPath = Environment.GetEnvironmentVariable("NETHERMIND_MDBX_LIBRARY")
            ?? Environment.GetEnvironmentVariable("MDBX_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && NativeLibrary.TryLoad(configuredPath, out IntPtr configuredHandle))
        {
            return configuredHandle;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDirectory, "runtimes", "linux-x64", "native", "libmdbx.so"),
            Path.Combine(baseDirectory, "libmdbx.so"),
            "libmdbx.so",
            "libmdbx.so.0",
            LibraryName,
        ];

        for (int i = 0; i < candidates.Length; i++)
        {
            if (NativeLibrary.TryLoad(candidates[i], assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    internal static void ThrowOnError(int result, string operation)
    {
        if (result == Success)
        {
            return;
        }

        throw new MdbxException(result, $"{operation} failed: {GetErrorMessage(result)}");
    }

    internal static string GetErrorMessage(int errorCode)
    {
        IntPtr message = StrError(errorCode);
        return message == IntPtr.Zero ? $"MDBX error {errorCode}" : Marshal.PtrToStringUTF8(message) ?? $"MDBX error {errorCode}";
    }

    internal static byte[] ToUtf8Z(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[] bytes = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    [DllImport(LibraryName, EntryPoint = "mdbx_env_create")]
    internal static extern int EnvCreate(out SafeMdbxEnvHandle env);

    [DllImport(LibraryName, EntryPoint = "mdbx_env_open")]
    internal static extern unsafe int EnvOpen(SafeMdbxEnvHandle env, byte* path, uint flags, uint mode);

    [DllImport(LibraryName, EntryPoint = "mdbx_env_set_geometry")]
    internal static extern int EnvSetGeometry(SafeMdbxEnvHandle env, nint sizeLower, nint sizeNow, nint sizeUpper, nint growthStep, nint shrinkThreshold, nint pageSize);

    [DllImport(LibraryName, EntryPoint = "mdbx_env_set_option")]
    private static extern int EnvSetOption(SafeMdbxEnvHandle env, uint option, ulong value);

    [DllImport(LibraryName, EntryPoint = "mdbx_env_sync_ex")]
    internal static extern int EnvSyncEx(SafeMdbxEnvHandle env, [MarshalAs(UnmanagedType.I1)] bool force, [MarshalAs(UnmanagedType.I1)] bool nonblock);

    [DllImport(LibraryName, EntryPoint = "mdbx_env_info_ex")]
    internal static extern int EnvInfoEx(SafeMdbxEnvHandle env, IntPtr txn, out MdbxEnvInfo info, nuint bytes);

    [DllImport(LibraryName, EntryPoint = "mdbx_env_close_ex")]
    private static extern int EnvCloseEx(IntPtr env, [MarshalAs(UnmanagedType.I1)] bool dontSync);

    [DllImport(LibraryName, EntryPoint = "mdbx_txn_begin")]
    internal static extern int TxnBegin(SafeMdbxEnvHandle env, IntPtr parent, uint flags, out SafeMdbxTxnHandle txn);

    [DllImport(LibraryName, EntryPoint = "mdbx_txn_commit")]
    private static extern int TxnCommit(IntPtr txn);

    [DllImport(LibraryName, EntryPoint = "mdbx_txn_abort")]
    private static extern int TxnAbort(IntPtr txn);

    [DllImport(LibraryName, EntryPoint = "mdbx_txn_reset")]
    internal static extern int TxnReset(SafeMdbxTxnHandle txn);

    [DllImport(LibraryName, EntryPoint = "mdbx_txn_renew")]
    internal static extern int TxnRenew(SafeMdbxTxnHandle txn);

    [DllImport(LibraryName, EntryPoint = "mdbx_dbi_open")]
    internal static extern unsafe int DbiOpen(SafeMdbxTxnHandle txn, byte* name, uint flags, out uint dbi);

    [DllImport(LibraryName, EntryPoint = "mdbx_drop")]
    internal static extern int Drop(SafeMdbxTxnHandle txn, uint dbi, [MarshalAs(UnmanagedType.I1)] bool delete);

    [DllImport(LibraryName, EntryPoint = "mdbx_get")]
    internal static extern int Get(SafeMdbxTxnHandle txn, uint dbi, ref MdbxValue key, out MdbxValue data);

    [DllImport(LibraryName, EntryPoint = "mdbx_put")]
    internal static extern int Put(SafeMdbxTxnHandle txn, uint dbi, ref MdbxValue key, ref MdbxValue data, uint flags);

    [DllImport(LibraryName, EntryPoint = "mdbx_del")]
    internal static extern int Del(SafeMdbxTxnHandle txn, uint dbi, ref MdbxValue key, IntPtr data);

    [DllImport(LibraryName, EntryPoint = "mdbx_cursor_open")]
    internal static extern int CursorOpen(SafeMdbxTxnHandle txn, uint dbi, out SafeMdbxCursorHandle cursor);

    [DllImport(LibraryName, EntryPoint = "mdbx_cursor_get")]
    internal static extern int CursorGet(SafeMdbxCursorHandle cursor, ref MdbxValue key, ref MdbxValue data, MdbxCursorOp operation);

    [DllImport(LibraryName, EntryPoint = "mdbx_dbi_stat")]
    internal static extern int DbiStat(SafeMdbxTxnHandle txn, uint dbi, out MdbxStat stat, nuint bytes);

    [DllImport(LibraryName, EntryPoint = "mdbx_cursor_close")]
    private static extern void CursorClose(IntPtr cursor);

    [DllImport(LibraryName, EntryPoint = "mdbx_strerror")]
    private static extern IntPtr StrError(int errorCode);

    internal static void SetMaxDbs(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionMaxDbs, value), "mdbx_env_set_option(MDBX_opt_max_db)");

    internal static void SetMaxReaders(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionMaxReaders, value), "mdbx_env_set_option(MDBX_opt_max_readers)");

    internal static void SetRpAugmentLimit(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionRpAugmentLimit, value), "mdbx_env_set_option(MDBX_opt_rp_augment_limit)");

    internal static void SetSyncBytes(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionSyncBytes, value), "mdbx_env_set_option(MDBX_opt_sync_bytes)");

    internal static void SetSyncPeriod(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionSyncPeriod, value), "mdbx_env_set_option(MDBX_opt_sync_period)");

    internal static void SetDirtyPagesReserveLimit(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionDpReserveLimit, value), "mdbx_env_set_option(MDBX_opt_dp_reserve_limit)");

    internal static void SetTransactionDirtyPagesLimit(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionTxnDpLimit, value), "mdbx_env_set_option(MDBX_opt_txn_dp_limit)");

    internal static void SetTransactionDirtyPagesInitial(SafeMdbxEnvHandle env, ulong value) =>
        ThrowOnError(EnvSetOption(env, EnvOptionTxnDpInitial, value), "mdbx_env_set_option(MDBX_opt_txn_dp_initial)");

    internal static void Commit(SafeMdbxTxnHandle txn)
    {
        int result = TxnCommit(txn.DangerousGetHandle());
        txn.MarkClosed();
        ThrowOnError(result, "mdbx_txn_commit");
    }

    internal sealed class SafeMdbxEnvHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle()
        {
            EnvCloseEx(handle, dontSync: false);
            return true;
        }
    }

    internal sealed class SafeMdbxTxnHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        internal void MarkClosed() => SetHandleAsInvalid();

        protected override bool ReleaseHandle()
        {
            TxnAbort(handle);
            return true;
        }
    }

    internal sealed class SafeMdbxCursorHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle()
        {
            CursorClose(handle);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MdbxStat
    {
        public uint PageSize;
        public uint Depth;
        public ulong BranchPages;
        public ulong LeafPages;
        public ulong OverflowPages;
        public ulong Entries;
        public ulong ModTxnId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MdbxEnvInfo
    {
        public MdbxGeometryInfo Geometry;
        public ulong MapSize;
        public ulong DatabaseFileSize;
        public ulong DatabaseAllocatedSize;
        public ulong LastPageNumber;
        public ulong RecentTransactionId;
        public ulong LatterReaderTransactionId;
        public ulong SelfLatterReaderTransactionId;
        public ulong MetaTransactionId0;
        public ulong MetaTransactionId1;
        public ulong MetaTransactionId2;
        public ulong MetaSign0;
        public ulong MetaSign1;
        public ulong MetaSign2;
        public uint MaxReaders;
        public uint NumReaders;
        public uint DxbPageSize;
        public uint SystemPageSize;
        public uint SystemUnifiedPageCacheBlockSize;
        public uint SystemIoBlockSize;
        public MdbxBootIdInfo BootId;
        public ulong UnsyncVolume;
        public ulong AutosyncThreshold;
        public uint SinceSyncSeconds16Dot16;
        public uint AutosyncPeriodSeconds16Dot16;
        public uint SinceReaderCheckSeconds16Dot16;
        public uint Mode;
        public MdbxPageOperationStat PageOperationStat;
        public MdbxDxbId DxbId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MdbxGeometryInfo
    {
        public ulong Lower;
        public ulong Upper;
        public ulong Current;
        public ulong Shrink;
        public ulong Grow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MdbxBootIdInfo
    {
        public MdbxPair Current;
        public MdbxPair Meta0;
        public MdbxPair Meta1;
        public MdbxPair Meta2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MdbxPageOperationStat
    {
        public ulong Newly;
        public ulong Cow;
        public ulong Clone;
        public ulong Split;
        public ulong Merge;
        public ulong Spill;
        public ulong Unspill;
        public ulong WriteOperations;
        public ulong Prefault;
        public ulong Mincore;
        public ulong Msync;
        public ulong Fsync;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MdbxDxbId
    {
        public ulong X;
        public ulong Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MdbxPair
    {
        public ulong X;
        public ulong Y;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MdbxValue
{
    public IntPtr Base;
    public nuint Length;
}

internal enum MdbxCursorOp
{
    First = 0,
    FirstDup = 1,
    GetBoth = 2,
    GetBothRange = 3,
    GetCurrent = 4,
    GetMultiple = 5,
    Last = 6,
    LastDup = 7,
    Next = 8,
    NextDup = 9,
    NextMultiple = 10,
    NextNoDup = 11,
    Prev = 12,
    PrevDup = 13,
    PrevNoDup = 14,
    Set = 15,
    SetKey = 16,
    SetRange = 17,
}

internal sealed class MdbxException(int errorCode, string message) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}
