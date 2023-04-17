// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RocksDbSharp;
using RocksDbNative = RocksDbSharp.Native;

namespace Nethermind.Db.Rocks;

internal static class RocksDbExtensions
{
    private static readonly ReadOptions _defaultReadOptions = new();

    internal static unsafe void DangerousReleaseMemory(this RocksDb _, in ReadOnlySpan<byte> span)
    {
        ref var ptr = ref MemoryMarshal.GetReference(span);
        var intPtr = new IntPtr(Unsafe.AsPointer(ref ptr));

        RocksDbNative.Instance.rocksdb_free(intPtr);
    }

    internal static unsafe Span<byte> GetSpan(this RocksDb db, ReadOnlySpan<byte> key, ColumnFamilyHandle? cf = null)
    {
        var readOptions = _defaultReadOptions.Handle;
        var keyLength = (long)key.Length;

        if (keyLength == 0)
            keyLength = key.Length;

        nint result;
        nint error;
        UIntPtr valueLength;

        fixed (byte* ptr = key)
        {
            var keyLengthPtr = (UIntPtr)keyLength;
            result = cf is null
                ? RocksDbNative.Instance.rocksdb_get(db.Handle, readOptions, ptr, keyLengthPtr, out valueLength, out error)
                : RocksDbNative.Instance.rocksdb_get_cf(db.Handle, readOptions, cf.Handle, ptr, keyLengthPtr, out valueLength, out error);

        }

        if (error != IntPtr.Zero)
            throw new RocksDbException(error);

        if (result == IntPtr.Zero)
            return default;

        var span = new Span<byte>((void*)result, (int)valueLength);

        return span;
    }
}
