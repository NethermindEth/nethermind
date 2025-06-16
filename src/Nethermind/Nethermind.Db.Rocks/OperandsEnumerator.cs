// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

// Taken from RocksDbSharp.MergeOperatorImpl
// TODO: minimize allocations?
internal class MergeOperatorAdapter(IMergeOperator inner) : MergeOperator
{
    public string Name => inner.Name;

    unsafe IntPtr MergeOperator.PartialMerge(
        IntPtr keyPtr,
        UIntPtr keyLength,
        IntPtr operandsList,
        IntPtr operandsListLength,
        int numOperands,
        out IntPtr successPtr,
        out IntPtr resultLength)
    {
        var key = new ReadOnlySpan<byte>((void*)keyPtr, (int)keyLength);
        var operands = new OperandsEnumerator(new((void*)operandsList, numOperands), new((void*)operandsListLength, numOperands));

        byte[] result = inner.PartialMerge(key, operands, out var success);

        IntPtr destination = Marshal.AllocHGlobal(result.Length);
        Marshal.Copy(result, 0, destination, result.Length);

        resultLength = result.Length;
        successPtr = Convert.ToInt32(success);

        return destination;
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
        var operands = new OperandsEnumerator(new((void*)operandsList, numOperands), new((void*)operandsListLength, numOperands));

        bool hasExistingValue = existingValuePtr != IntPtr.Zero;
        ReadOnlySpan<byte> existingValue = hasExistingValue ? new((void*)existingValuePtr, (int)existingValueLength) : ReadOnlySpan<byte>.Empty;

        byte[] result = inner.FullMerge(key, hasExistingValue, existingValue, operands, out var success);

        IntPtr destination = Marshal.AllocHGlobal(result.Length);
        Marshal.Copy(result, 0, destination, result.Length);

        resultLength = result.Length;
        successPtr = Convert.ToInt32(success);

        return destination;
    }

    // TODO: use ArrayPool instead of doing alloc and copy?
    void MergeOperator.DeleteValue(IntPtr value, UIntPtr valueLength) => Marshal.FreeHGlobal(value);
}
