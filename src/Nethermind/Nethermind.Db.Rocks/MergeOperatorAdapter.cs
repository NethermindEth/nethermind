// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

// Also see RocksDbSharp.MergeOperatorImpl
internal class MergeOperatorAdapter(IMergeOperator inner) : MergeOperator
{
    public string Name => inner.Name;

    // TODO: fix and return array ptr instead of copying to unmanaged memory?
    private static unsafe IntPtr GetResult(ArrayPoolList<byte>? data, out IntPtr resultLength, out IntPtr success)
    {
        if (data is null)
        {
            success = Convert.ToInt32(false);
            resultLength = IntPtr.Zero;
            return IntPtr.Zero;
        }

        using (data)
        {
            void* resultPtr = NativeMemory.Alloc((uint)data.Count);
            Console.WriteLine($"Allocated {data.Count} bytes at {(IntPtr)resultPtr:x}");

            var result = new Span<byte>(resultPtr, data.Count);
            data.AsSpan().CopyTo(result);
            Console.WriteLine($"Copied {Convert.ToHexString(data.AsSpan())} to {(IntPtr)resultPtr:x}");

            resultLength = data.Count;
            success = Convert.ToInt32(true);

            Console.WriteLine($"Returning ({resultLength}, {success})");
            return (IntPtr)resultPtr;
        }
    }

    unsafe IntPtr MergeOperator.PartialMerge(
        IntPtr keyPtr,
        UIntPtr keyLength,
        IntPtr operandsList,
        IntPtr operandsListLength,
        int numOperands,
        out IntPtr successPtr,
        out IntPtr resultLength)
    {
        var key = new Span<byte>((void*)keyPtr, (int)keyLength);
        var enumerator = new RocksDbMergeEnumerator(new((void*)operandsList, numOperands), new((void*)operandsListLength, numOperands));

        ArrayPoolList<byte>? result = inner.PartialMerge(key, enumerator);
        return GetResult(result, out resultLength, out successPtr);
    }

    unsafe IntPtr MergeOperator.FullMerge(
        IntPtr keyPtr,
        UIntPtr keyLength,
        IntPtr existingValuePtr,
        UIntPtr existingValueLength,
        IntPtr operandsList,
        IntPtr operandsListLength,
        int numOperands,
        out IntPtr successPtr,
        out IntPtr resultLength)
    {
        var key = new ReadOnlySpan<byte>((void*)keyPtr, (int)keyLength);
        bool hasExistingValue = existingValuePtr != IntPtr.Zero;
        Span<byte> existingValue = hasExistingValue ? new((void*)existingValuePtr, (int)existingValueLength) : Span<byte>.Empty;
        var enumerator = new RocksDbMergeEnumerator(existingValue, hasExistingValue, new((void*)operandsList, numOperands), new((void*)operandsListLength, numOperands));

        ArrayPoolList<byte>? result = inner.FullMerge(key, enumerator);
        return GetResult(result, out resultLength, out successPtr);
    }

    unsafe void MergeOperator.DeleteValue(IntPtr value, UIntPtr valueLength) => NativeMemory.Free((void*)value);
}
