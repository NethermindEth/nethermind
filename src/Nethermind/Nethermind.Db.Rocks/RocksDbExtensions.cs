//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RocksDbSharp;
using RocksDbNative = RocksDbSharp.Native;

namespace Nethermind.Db.Rocks;

internal static class RocksDbExtensions
{
    private static readonly ReadOptions _defaultReadOptions = new();

    internal static unsafe void DangerousReleaseMemory(this RocksDb _, in Span<byte> span)
    {
        ref var ptr = ref MemoryMarshal.GetReference(span);
        var intPtr = new IntPtr(Unsafe.AsPointer(ref ptr));

        RocksDbNative.Instance.rocksdb_free(intPtr);
    }

    internal static unsafe Span<byte> GetSpan(this RocksDb db, byte[] key, ColumnFamilyHandle? cf = null)
    {
        var readOptions = _defaultReadOptions.Handle;
        var keyLength = key.GetLongLength(0);

        if (keyLength == 0)
            keyLength = key.Length;

        var keyLengthPtr = (UIntPtr)keyLength;
        var result = cf == null
            ? RocksDbNative.Instance.rocksdb_get(db.Handle, readOptions, key, keyLengthPtr, out var valueLength, out var error)
            : RocksDbNative.Instance.rocksdb_get_cf(db.Handle, readOptions, cf.Handle, key, keyLengthPtr, out valueLength, out error);

        if (error != IntPtr.Zero)
            throw new RocksDbException(error);

        if (result == IntPtr.Zero)
            return null;

        var span = new Span<byte>((void*)result, (int)valueLength);

        return span; 
    }
}
