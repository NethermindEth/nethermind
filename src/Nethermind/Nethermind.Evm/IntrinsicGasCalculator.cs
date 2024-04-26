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
using Nethermind.Core.Extensions;

namespace Nethermind.Evm;

public static class IntrinsicGasCalculator
{
    public static long Calculate(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long result = GasCostOf.Transaction;
        result += DataCost(transaction, releaseSpec);
        result += CreateCost(transaction, releaseSpec);
        result += AccessListCost(transaction, releaseSpec);
        result += EofInitCodeCost(transaction, releaseSpec);
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

    private static long EofInitCodeCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        if(releaseSpec.IsEofEnabled && transaction.IsEofContractCreation)
        {
            long initcodeCosts = 0;
            foreach(var initcode in transaction.Initcodes)
            {
                initcodeCosts += CalculateCalldataCost(initcode, releaseSpec);
            }
            return initcodeCosts;
        }
        return 0;
    }

    private static long DataCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        Span<byte> data = transaction.Data.GetValueOrDefault().Span;
        long dataCost = CalculateCalldataCost(data, releaseSpec);

        if (transaction.IsContractCreation && releaseSpec.IsEip3860Enabled)
        {
            dataCost += EvmPooledMemory.Div32Ceiling((UInt256)data.Length) * GasCostOf.InitCodeWord;
        }

        return dataCost;
    }

    private static long CalculateCalldataCost(Span<byte> data, IReleaseSpec releaseSpec)
    {
        long txDataNonZeroGasCost =
            releaseSpec.IsEip2028Enabled ? GasCostOf.TxDataNonZeroEip2028 : GasCostOf.TxDataNonZero;
        Span<byte> data = transaction.Data.GetValueOrDefault().Span;

        int totalZeros = data.CountZeros();

        var baseDataCost = (transaction.IsContractCreation && releaseSpec.IsEip3860Enabled
            ? EvmPooledMemory.Div32Ceiling((UInt256)data.Length) * GasCostOf.InitCodeWord
            : 0);

        return baseDataCost +
            totalZeros * GasCostOf.TxDataZero +
            (data.Length - totalZeros) * txDataNonZeroGasCost;
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
