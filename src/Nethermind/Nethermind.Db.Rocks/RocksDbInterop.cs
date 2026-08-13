// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Nethermind.RocksDbBindings;
using Nethermind.RocksDbBindings.Native;

using static Nethermind.RocksDbBindings.Native.RocksDbNative;

namespace Nethermind.Db.Rocks;

internal static class RocksDbInterop
{
    // RocksDbNativeException takes ownership of non-null error pointers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ThrowIfError(sbyte* errPtr)
    {
        if (errPtr is not null) Throw(errPtr);

        [DoesNotReturn, StackTraceHidden]
        static void Throw(sbyte* errPtr) => throw new RocksDbNativeException((nint)errPtr);
    }

    public static unsafe void SetOptionsCf(nint db, nint columnFamily, ReadOnlySpan<string> keys, ReadOnlySpan<string> values)
    {
        if (keys.Length != values.Length)
            throw new ArgumentException($"Expected as many values as keys, got {keys.Length} keys and {values.Length} values.", nameof(values));

        int count = keys.Length;
        // RocksDB consumes the strings during this call; zeroing keeps partial-conversion cleanup safe.
        byte** utf8 = (byte**)NativeMemory.AllocZeroed((nuint)(count * 2), (nuint)sizeof(byte*));
        try
        {
            for (int i = 0; i < count; i++)
            {
                utf8[i] = Utf8StringMarshaller.ConvertToUnmanaged(keys[i]);
                utf8[count + i] = Utf8StringMarshaller.ConvertToUnmanaged(values[i]);
            }

            sbyte* errPtr = null;
            rocksdb_set_options_cf(
                (rocksdb_t*)db, (rocksdb_column_family_handle_t*)columnFamily, count, (sbyte**)utf8, (sbyte**)(utf8 + count), &errPtr);
            ThrowIfError(errPtr);
        }
        finally
        {
            for (int i = 0; i < count * 2; i++)
            {
                Utf8StringMarshaller.Free(utf8[i]);
            }

            NativeMemory.Free(utf8);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetMaxSubcompactions(nint options, uint value) =>
        rocksdb_options_set_max_subcompactions((rocksdb_options_t*)options, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe nint CreateLruCache(nuint capacity) =>
        (nint)rocksdb_cache_create_lru(capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int GetNumLevels(nint options) =>
        rocksdb_options_get_num_levels((rocksdb_options_t*)options);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetRowCache(nint options, nint cache) =>
        rocksdb_options_set_row_cache((rocksdb_options_t*)options, (rocksdb_cache_t*)cache);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SetLowPriority(WriteOptions options) =>
        rocksdb_writeoptions_set_low_pri((rocksdb_writeoptions_t*)options.Handle, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe nuint GetWriteBatchSize(WriteBatch batch)
    {
        nuint size;
        rocksdb_writebatch_data((rocksdb_writebatch_t*)batch.Handle, &size);
        return size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void DestroyCache(nint cache) =>
        rocksdb_cache_destroy((rocksdb_cache_t*)cache);

    // RocksDB reads the bounds while iterating instead of copying them, so the caller owns both pointers
    // and must NativeMemory.Free them once the iterator and read options are disposed.
    public static unsafe void SetIterateBounds(
        ReadOptions readOptions, ReadOnlySpan<byte> lowerBound, ReadOnlySpan<byte> upperBound, out nint lower, out nint upper)
    {
        byte* lowerPtr = AllocateCopy(lowerBound);
        byte* upperPtr = AllocateCopy(upperBound);
        lower = (nint)lowerPtr;
        upper = (nint)upperPtr;

        rocksdb_readoptions_t* handle = (rocksdb_readoptions_t*)readOptions.Handle;
        rocksdb_readoptions_set_iterate_lower_bound(handle, (sbyte*)lowerPtr, (nuint)lowerBound.Length);
        rocksdb_readoptions_set_iterate_upper_bound(handle, (sbyte*)upperPtr, (nuint)upperBound.Length);

        static byte* AllocateCopy(ReadOnlySpan<byte> value)
        {
            byte* buffer = (byte*)NativeMemory.Alloc((nuint)value.Length);
            value.CopyTo(new Span<byte>(buffer, value.Length));
            return buffer;
        }
    }

    // Suppresses the finalizer too: short-lived ReadOptions would otherwise build up the finalizer queue.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void DestroyReadOptions(ReadOptions options)
    {
        rocksdb_readoptions_destroy((rocksdb_readoptions_t*)options.Handle);
        GC.SuppressFinalize(options);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void DestroyPinnableSlice(nint handle)
    {
        if (handle != 0)
            rocksdb_pinnableslice_destroy((rocksdb_pinnableslice_t*)handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void FlushWal(RocksDb db)
    {
        sbyte* errPtr = null;
        rocksdb_flush_wal((rocksdb_t*)db.Handle, 1, &errPtr);
        ThrowIfError(errPtr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void FlushColumnFamily(
        RocksDb db,
        FlushOptions flushOptions,
        ColumnFamilyHandle columnFamily)
    {
        sbyte* errPtr = null;
        rocksdb_flush_cf(
            (rocksdb_t*)db.Handle,
            (rocksdb_flushoptions_t*)flushOptions.Handle,
            (rocksdb_column_family_handle_t*)columnFamily.Handle,
            &errPtr);
        ThrowIfError(errPtr);
    }
}
