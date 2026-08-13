// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.RocksDbBindings;
using Nethermind.RocksDbBindings.Native;

using static Nethermind.RocksDbBindings.Native.RocksDbNative;

namespace Nethermind.Db.Rocks;

internal static class RocksDbExtensions
{
    private static readonly ReadOptions _defaultReadOptions = new();

    // Only buffers returned by GetSpan may be released here, exactly once.
    internal static unsafe void DangerousReleaseMemory(this RocksDb _, in ReadOnlySpan<byte> span)
    {
        ref byte ptr = ref MemoryMarshal.GetReference(span);

        rocksdb_free(Unsafe.AsPointer(ref ptr));
    }

    // The returned buffer stays native-owned until released through DangerousReleaseMemory.
    internal static unsafe Span<byte> GetSpan(this RocksDb db, scoped ReadOnlySpan<byte> key, ColumnFamilyHandle? cf = null, ReadOptions? readOptionObj = null)
    {
        rocksdb_readoptions_t* readOptions = (rocksdb_readoptions_t*)(readOptionObj ?? _defaultReadOptions).Handle;

        sbyte* result;
        sbyte* error = null;
        nuint valueLength;

        fixed (byte* ptr = key)
        {
            nuint keyLength = (nuint)key.Length;
            result = cf is null
                ? rocksdb_get((rocksdb_t*)db.Handle, readOptions, (sbyte*)ptr, keyLength, &valueLength, &error)
                : rocksdb_get_cf((rocksdb_t*)db.Handle, readOptions, (rocksdb_column_family_handle_t*)cf.Handle, (sbyte*)ptr, keyLength, &valueLength, &error);
        }

        RocksDbInterop.ThrowIfError(error);

        if (result is null)
            return default;

        return new Span<byte>(result, (int)valueLength);
    }
}
