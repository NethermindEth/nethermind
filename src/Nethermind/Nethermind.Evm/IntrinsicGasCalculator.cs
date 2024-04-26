// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public static class IntrinsicGasCalculator
{
    public static long Calculate(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long result = GasCostOf.Transaction;
        result += DataCost(transaction, releaseSpec);
        result += CreateCost(transaction, releaseSpec);
        result += AccessListCost(transaction, releaseSpec);
        return result;
    }

    private static long CreateCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long createCost = 0;
        if (transaction.IsContractCreation && releaseSpec.IsEip2Enabled)
        {
            createCost += GasCostOf.TxCreate;
        }

        return createCost;
    }

    private static long DataCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long txDataNonZeroGasCost =
            releaseSpec.IsEip2028Enabled ? GasCostOf.TxDataNonZeroEip2028 : GasCostOf.TxDataNonZero;
        Span<byte> data = transaction.Data.GetValueOrDefault().Span;

        var dataCost = transaction.IsContractCreation && releaseSpec.IsEip3860Enabled
            ? EvmPooledMemory.Div32Ceiling((UInt256)data.Length) * GasCostOf.InitCodeWord
            : 0;

        if (Vector256.IsHardwareAccelerated && data.Length >= Vector256<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i < data.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> dataVector = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref bytes, i));
                uint flags = Vector256.Equals(dataVector, default).ExtractMostSignificantBits();
                var zeros = BitOperations.PopCount(flags);
                dataCost += zeros * GasCostOf.TxDataZero + (Vector256<byte>.Count - zeros) * txDataNonZeroGasCost;
            }

            data = data[i..];
        }
        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            ref byte bytes = ref MemoryMarshal.GetReference(data);
            int i = 0;
            for (; i < data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> dataVector = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), i));
                uint flags = Vector128.Equals(dataVector, default).ExtractMostSignificantBits();
                var zeros = BitOperations.PopCount(flags);
                dataCost += zeros * GasCostOf.TxDataZero + (Vector128<byte>.Count - zeros) * txDataNonZeroGasCost;
            }

            data = data[i..];
        }

        for (int i = 0; i < data.Length; i++)
        {
            dataCost += data[i] == 0 ? GasCostOf.TxDataZero : txDataNonZeroGasCost;
        }

        return dataCost;
    }

    private static long AccessListCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        AccessList? accessList = transaction.AccessList;
        long accessListCost = 0;
        if (accessList is not null)
        {
            if (!releaseSpec.UseTxAccessLists)
            {
                throw new InvalidDataException(
                    $"Transaction with an access list received within the context of {releaseSpec.Name}. Eip-2930 is not enabled.");
            }

            if (accessList.IsEmpty) return accessListCost;

            foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) entry in accessList)
            {
                accessListCost += GasCostOf.AccessAccountListEntry;
                foreach (UInt256 _ in entry.storageKeys)
                {
                    accessListCost += GasCostOf.AccessStorageListEntry;
                }
            }
        }

        return accessListCost;
    }
}
