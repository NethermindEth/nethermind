// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;

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
        long dataCost = 0;
        if (transaction.Data is not null)
        {
            Span<byte> data = transaction.Data.Value.Span;
            for (int i = 0; i < transaction.DataLength; i++)
            {
                dataCost += data[i] == 0 ? GasCostOf.TxDataZero : txDataNonZeroGasCost;
            }
        }

        if (transaction.IsContractCreation && releaseSpec.IsEip3860Enabled)
        {
            dataCost += EvmPooledMemory.Div32Ceiling((UInt256)transaction.DataLength) * GasCostOf.InitCodeWord;
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

            foreach ((Address Address, IEnumerable<UInt256> StorageKeys) entry in accessList.AsEnumerable())
            {
                accessListCost += GasCostOf.AccessAccountListEntry;
                foreach (UInt256 _ in entry.StorageKeys)
                {
                    accessListCost += GasCostOf.AccessStorageListEntry;
                }
            }
        }

        return accessListCost;
    }
}
