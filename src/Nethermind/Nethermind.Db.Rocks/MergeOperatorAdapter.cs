// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

// Also see RocksDbSharp.MergeOperatorImpl
internal class MergeOperatorAdapter(IMergeOperator inner) : MergeOperator
{
    public string Name => inner.Name;

    // TODO: fix and return array ptr instead of copying to unmanaged memory?
    private static unsafe nint GetResult(ArrayPoolList<byte>? data, out nint resultLength, out byte success)
    {
        if (data is null)
        {
            success = 0;
            resultLength = nint.Zero;
            return nint.Zero;
        }

        using (data)
        {
            void* resultPtr = NativeMemory.Alloc((uint)data.Count);
            var result = new Span<byte>(resultPtr, data.Count);
            data.AsSpan().CopyTo(result);

            resultLength = result.Length;

            // Fixing RocksDbSharp invalid callback signature, TODO: submit an issue/PR
            Unsafe.SkipInit(out success);
            Unsafe.As<byte, byte>(ref success) = 1;

            return (nint)resultPtr;
        }
    }

    public unsafe nint PartialMerge(nint key, nuint keyLength, nint operandsList, nint operandsListLength, int numOperands, out byte success, out nint newValueLength)
    {
        var keyBytes = new Span<byte>((void*)key, (int)keyLength);
        var enumerator = new RocksDbMergeEnumerator(new((void*)operandsList, numOperands), new((void*)operandsListLength, numOperands));

        ArrayPoolList<byte>? result = inner.PartialMerge(keyBytes, enumerator);
        return GetResult(result, out newValueLength, out success);
    }

    public unsafe nint FullMerge(nint key, nuint keyLength, nint existingValue, nuint existingValueLength, nint operandsList, nint operandsListLength, int numOperands, out byte success, out nint newValueLength)
    {
        var keyBytes = new ReadOnlySpan<byte>((void*)key, (int)keyLength);
        bool hasExistingValue = existingValue != nint.Zero;
        Span<byte> existingValueBytes = hasExistingValue ? new((void*)existingValue, (int)existingValueLength) : [];
        var enumerator = new RocksDbMergeEnumerator(existingValueBytes, hasExistingValue, new((void*)operandsList, numOperands), new((void*)operandsListLength, numOperands));

        ArrayPoolList<byte>? result = inner.FullMerge(keyBytes, enumerator);
        return GetResult(result, out newValueLength, out success);
    }

    unsafe void MergeOperator.DeleteValue(nint value, nuint valueLength) => NativeMemory.Free((void*)value);
}
