// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RocksDbSharp;
using RocksDbSharp.Native;
using RocksNative = RocksDbSharp.Native.RocksDbNative;

namespace Nethermind.Db.Rocks;

public sealed unsafe class Native
{
    public static Native Instance { get; } = new();

    private Native()
    {
    }

    public void rocksdb_repair_db(IntPtr options, string? name)
    {
        ArgumentNullException.ThrowIfNull(name);

        byte* namePtr = Utf8StringMarshaller.ConvertToUnmanaged(name);
        try
        {
            sbyte* errPtr = null;
            RocksNative.rocksdb_repair_db((rocksdb_options_t*)options, (sbyte*)namePtr, &errPtr);
            ThrowIfError(errPtr);
        }
        finally
        {
            Utf8StringMarshaller.Free(namePtr);
        }
    }

    public void rocksdb_get_options_from_string(IntPtr baseOptions, IntPtr opts, IntPtr newOptions)
    {
        sbyte* errPtr = null;
        RocksNative.rocksdb_get_options_from_string((rocksdb_options_t*)baseOptions, (sbyte*)opts, (rocksdb_options_t*)newOptions, &errPtr);
        ThrowIfError(errPtr);
    }

    public void rocksdb_options_set_max_subcompactions(IntPtr options, uint value) =>
        RocksNative.rocksdb_options_set_max_subcompactions((rocksdb_options_t*)options, value);

    public IntPtr rocksdb_cache_create_lru(UIntPtr capacity) =>
        (IntPtr)RocksNative.rocksdb_cache_create_lru((nuint)capacity);

    public void rocksdb_options_set_row_cache(IntPtr options, IntPtr cache) =>
        RocksNative.rocksdb_options_set_row_cache((rocksdb_options_t*)options, (rocksdb_cache_t*)cache);

    public void rocksdb_writeoptions_set_low_pri(IntPtr writeOptions, byte value) =>
        RocksNative.rocksdb_writeoptions_set_low_pri((rocksdb_writeoptions_t*)writeOptions, value);

    public void rocksdb_flush_wal(IntPtr db, byte sync)
    {
        sbyte* errPtr = null;
        RocksNative.rocksdb_flush_wal((rocksdb_t*)db, sync, &errPtr);
        ThrowIfError(errPtr);
    }

    public void rocksdb_flush(IntPtr db, IntPtr options)
    {
        sbyte* errPtr = null;
        RocksNative.rocksdb_flush((rocksdb_t*)db, (rocksdb_flushoptions_t*)options, &errPtr);
        ThrowIfError(errPtr);
    }

    public void rocksdb_flush_cf(IntPtr db, IntPtr options, IntPtr columnFamily)
    {
        sbyte* errPtr = null;
        RocksNative.rocksdb_flush_cf((rocksdb_t*)db, (rocksdb_flushoptions_t*)options, (rocksdb_column_family_handle_t*)columnFamily, &errPtr);
        ThrowIfError(errPtr);
    }

    public void rocksdb_cache_destroy(IntPtr cache) =>
        RocksNative.rocksdb_cache_destroy((rocksdb_cache_t*)cache);

    public IntPtr rocksdb_flushoptions_create() =>
        (IntPtr)RocksNative.rocksdb_flushoptions_create();

    public void rocksdb_flushoptions_destroy(IntPtr options) =>
        RocksNative.rocksdb_flushoptions_destroy((rocksdb_flushoptions_t*)options);

    public IntPtr rocksdb_get_pinned(IntPtr db, IntPtr options, byte* key, UIntPtr keyLength, out IntPtr errPtr)
    {
        sbyte* error = null;
        rocksdb_pinnableslice_t* result = RocksNative.rocksdb_get_pinned((rocksdb_t*)db, (rocksdb_readoptions_t*)options, (sbyte*)key, (nuint)keyLength, &error);
        errPtr = (IntPtr)error;
        return (IntPtr)result;
    }

    public IntPtr rocksdb_get_pinned_cf(IntPtr db, IntPtr options, IntPtr columnFamily, byte* key, UIntPtr keyLength, out IntPtr errPtr)
    {
        sbyte* error = null;
        rocksdb_pinnableslice_t* result = RocksNative.rocksdb_get_pinned_cf((rocksdb_t*)db, (rocksdb_readoptions_t*)options, (rocksdb_column_family_handle_t*)columnFamily, (sbyte*)key, (nuint)keyLength, &error);
        errPtr = (IntPtr)error;
        return (IntPtr)result;
    }

    public IntPtr rocksdb_pinnableslice_value(IntPtr slice, out UIntPtr valueLength)
    {
        nuint length = 0;
        sbyte* result = RocksNative.rocksdb_pinnableslice_value((rocksdb_pinnableslice_t*)slice, &length);
        valueLength = (UIntPtr)length;
        return (IntPtr)result;
    }

    public void rocksdb_pinnableslice_destroy(IntPtr slice) =>
        RocksNative.rocksdb_pinnableslice_destroy((rocksdb_pinnableslice_t*)slice);

    public IntPtr rocksdb_writebatch_data(IntPtr batch, out UIntPtr size)
    {
        nuint length = 0;
        sbyte* result = RocksNative.rocksdb_writebatch_data((rocksdb_writebatch_t*)batch, &length);
        size = (UIntPtr)length;
        return (IntPtr)result;
    }

    public void rocksdb_readoptions_set_iterate_lower_bound(IntPtr readOptions, IntPtr key, UIntPtr keyLength) =>
        RocksNative.rocksdb_readoptions_set_iterate_lower_bound((rocksdb_readoptions_t*)readOptions, (sbyte*)key, (nuint)keyLength);

    public void rocksdb_readoptions_set_iterate_upper_bound(IntPtr readOptions, IntPtr key, UIntPtr keyLength) =>
        RocksNative.rocksdb_readoptions_set_iterate_upper_bound((rocksdb_readoptions_t*)readOptions, (sbyte*)key, (nuint)keyLength);

    public void rocksdb_readoptions_destroy(IntPtr readOptions) =>
        RocksNative.rocksdb_readoptions_destroy((rocksdb_readoptions_t*)readOptions);

    public IntPtr rocksdb_cache_create_hyper_clock(UIntPtr capacity, int estimatedEntryCharge) =>
        (IntPtr)RocksNative.rocksdb_cache_create_hyper_clock((nuint)capacity, (nuint)estimatedEntryCharge);

    public UIntPtr rocksdb_cache_get_usage(IntPtr cache) =>
        (UIntPtr)RocksNative.rocksdb_cache_get_usage((rocksdb_cache_t*)cache);

    public void rocksdb_free(IntPtr ptr) =>
        RocksNative.rocksdb_free((void*)ptr);

    public IntPtr rocksdb_get(IntPtr db, IntPtr options, byte* key, UIntPtr keyLength, out UIntPtr valueLength, out IntPtr errPtr)
    {
        nuint length = 0;
        sbyte* error = null;
        sbyte* result = RocksNative.rocksdb_get((rocksdb_t*)db, (rocksdb_readoptions_t*)options, (sbyte*)key, (nuint)keyLength, &length, &error);
        valueLength = (UIntPtr)length;
        errPtr = (IntPtr)error;
        return (IntPtr)result;
    }

    public IntPtr rocksdb_get_cf(IntPtr db, IntPtr options, IntPtr columnFamily, byte* key, UIntPtr keyLength, out UIntPtr valueLength, out IntPtr errPtr)
    {
        nuint length = 0;
        sbyte* error = null;
        sbyte* result = RocksNative.rocksdb_get_cf((rocksdb_t*)db, (rocksdb_readoptions_t*)options, (rocksdb_column_family_handle_t*)columnFamily, (sbyte*)key, (nuint)keyLength, &length, &error);
        valueLength = (UIntPtr)length;
        errPtr = (IntPtr)error;
        return (IntPtr)result;
    }

    public void rocksdb_set_options_cf(IntPtr db, IntPtr columnFamily, int count, string[] keys, string[] values)
    {
        using NativeStringArray keysArray = new(keys, count);
        using NativeStringArray valuesArray = new(values, count);

        sbyte* errPtr = null;
        RocksNative.rocksdb_set_options_cf((rocksdb_t*)db, (rocksdb_column_family_handle_t*)columnFamily, count, keysArray.Pointer, valuesArray.Pointer, &errPtr);
        ThrowIfError(errPtr);
    }

    private static void ThrowIfError(sbyte* errPtr)
    {
        if (errPtr is not null)
        {
            throw new RocksDbException((IntPtr)errPtr);
        }
    }

    private sealed class NativeStringArray : IDisposable
    {
        private readonly byte** _buffer;
        private readonly int _count;

        public NativeStringArray(string[] strings, int count)
        {
            _count = count;
            // Zeroed so that, on a partial failure below, the unfilled slots are null and Dispose skips them.
            _buffer = (byte**)NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(byte*));

            try
            {
                for (int i = 0; i < count; i++)
                {
                    _buffer[i] = Utf8StringMarshaller.ConvertToUnmanaged(strings[i]);
                }
            }
            catch
            {
                // The instance is never assigned to the caller's `using`, so free here to avoid leaking
                // the buffer and any strings allocated before the failure.
                Dispose();
                throw;
            }

            Pointer = (sbyte**)_buffer;
        }

        public sbyte** Pointer { get; }

        public void Dispose()
        {
            for (int i = 0; i < _count; i++)
            {
                Utf8StringMarshaller.Free(_buffer[i]);
            }

            NativeMemory.Free(_buffer);
        }
    }
}
