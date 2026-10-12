// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>The native flat DB store, running on the database of the flat columns.</summary>
/// <remarks>
/// Over a RocksDB <see cref="ColumnsDb{T}"/> the store drives the very instance C# opened, through the
/// <see cref="RocksApiTable"/>, and holds a native lease on it until the handle is released, so disposing the database
/// waits for the store rather than closing under it. The native side refuses to be destroyed while a reader, iterator
/// or batch of it is open, and the lease is then kept. Other databases need the managed key-value backend, which the
/// native library does not implement yet.
/// </remarks>
internal sealed unsafe class RustFlatStore : SafeHandle
{
    private static readonly int ColumnCount = Enum.GetValues<FlatDbColumns>().Length;

    private readonly ColumnsDb<FlatDbColumns>? _leasedDb;

    private RustFlatStore(nint store, ColumnsDb<FlatDbColumns>? leasedDb) : base(0, ownsHandle: true)
    {
        SetHandle(store);
        _leasedDb = leasedDb;
    }

    public override bool IsInvalid => handle == 0;

    /// <exception cref="InvalidOperationException">The native library is missing or incompatible.</exception>
    /// <exception cref="NotSupportedException"><paramref name="db"/> is not a RocksDB database.</exception>
    public static RustFlatStore Create(IColumnsDb<FlatDbColumns> db, IFlatDbConfig config)
    {
        NativeMethods.EnsureAvailable();
        return db is ColumnsDb<FlatDbColumns> rocksDb
            ? CreateOnRocksDb(rocksDb, config)
            : throw new NotSupportedException($"The Rust flat DB backend needs a RocksDB flat database, not {db.GetType().Name}, until it gains its managed key-value backend.");
    }

    private static RustFlatStore CreateOnRocksDb(ColumnsDb<FlatDbColumns> db, IFlatDbConfig config)
    {
        RocksApiTable api = RocksApiTable.Shared;
        db.AcquireNativeLease();
        try
        {
            nint* columnFamilies = stackalloc nint[ColumnCount];
            for (int i = 0; i < ColumnCount; i++)
            {
                columnFamilies[i] = ((ColumnDb)db.GetColumnDb((FlatDbColumns)i))._columnFamily.Handle;
            }

            FdbStoreCfg cfg = new() { AbiVersion = FfiAbi.Version, Layout = (uint)config.Layout };
            FdbKvDesc kv = new()
            {
                Kind = FdbKvKind.Rocks,
                ColumnCount = (uint)ColumnCount,
                RocksApi = api.Pointer,
                RocksDb = db._db.Handle,
                RocksColumnFamilies = columnFamilies,
                ReadaheadSize = db.ReadAheadSize,
                PrefixExtractorLength = (uint)db.PrefixExtractorLength,
                VerifyChecksums = db.VerifyChecksum ? (byte)1 : (byte)0,
                WalSync = db.WriteOptions!.GetSync() ? (byte)1 : (byte)0,
            };

            nint store;
            StatusMap.ThrowIfFailed(NativeMethods.fdb_store_create(&cfg, &kv, &store));
            return new RustFlatStore(store, db);
        }
        catch
        {
            db.ReleaseNativeLease();
            throw;
        }
    }

    protected override bool ReleaseHandle()
    {
        if (NativeMethods.fdb_store_destroy(handle) != FdbStatus.Ok) return false;

        _leasedDb?.ReleaseNativeLease();
        return true;
    }

    /// <summary>Adds the reads the native store made since the last call to the <c>TotalReads</c> metric of the database.</summary>
    internal void PublishReadMetrics()
    {
        bool added = false;
        try
        {
            DangerousAddRef(ref added);
            ulong count;
            StatusMap.ThrowIfFailed(NativeMethods.fdb_store_take_read_count(handle, &count));
            _leasedDb?.UpdateReadMetrics((int)Math.Min(count, int.MaxValue));
        }
        finally
        {
            if (added) DangerousRelease();
        }
    }

    /// <summary>Writes <paramref name="value"/> through the native store, in a batch of its own.</summary>
    internal void DebugPut(FlatDbColumns column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        bool added = false;
        try
        {
            DangerousAddRef(ref added);
            fixed (byte* keyPtr = key, valuePtr = value)
            {
                StatusMap.ThrowIfFailed(NativeMethods.fdb_debug_put(handle, (uint)column, keyPtr, (nuint)key.Length, valuePtr, (nuint)value.Length));
            }
        }
        finally
        {
            if (added) DangerousRelease();
        }
    }

    /// <summary>Reads the latest value of <paramref name="key"/> through the native store.</summary>
    internal byte[]? DebugGet(FlatDbColumns column, ReadOnlySpan<byte> key)
    {
        bool added = false;
        try
        {
            DangerousAddRef(ref added);
            byte[] buffer = new byte[256];
            while (true)
            {
                long length;
                FdbStatus status;
                fixed (byte* keyPtr = key, bufferPtr = buffer)
                {
                    status = NativeMethods.fdb_debug_get(handle, (uint)column, keyPtr, (nuint)key.Length, bufferPtr, (nuint)buffer.Length, &length);
                }

                if (status == FdbStatus.BufferTooSmall)
                {
                    buffer = new byte[length];
                    continue;
                }

                StatusMap.ThrowIfFailed(status);
                return length < 0 ? null : buffer.AsSpan(0, (int)length).ToArray();
            }
        }
        finally
        {
            if (added) DangerousRelease();
        }
    }
}
