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
        result += AccessListCost(transaction);
        result += AuthorizationListCost(transaction);
        return result;
    }

    private static long CreateCost(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.IsContractCreation && releaseSpec.IsEip2Enabled ? GasCostOf.TxCreate : 0;

    private static long DataCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long txDataNonZeroGasCost = releaseSpec.IsEip2028Enabled ? GasCostOf.TxDataNonZeroEip2028 : GasCostOf.TxDataNonZero;
        Span<byte> data = transaction.Data.GetValueOrDefault().Span;

        int totalZeros = data.CountZeros();

        var baseDataCost = transaction.IsContractCreation && releaseSpec.IsEip3860Enabled
            ? EvmPooledMemory.Div32Ceiling((UInt256)data.Length) * GasCostOf.InitCodeWord
            : 0;

        return baseDataCost +
            totalZeros * GasCostOf.TxDataZero +
            (data.Length - totalZeros) * txDataNonZeroGasCost;
    }

    private static long AccessListCost(Transaction transaction)
    {
        (int addressesCount, int storageKeysCount) = transaction.AccessList?.Count ?? (0, 0);
        return addressesCount * GasCostOf.AccessAccountListEntry + storageKeysCount * GasCostOf.AccessAccountListEntry;
    }

    private static long AuthorizationListCost(Transaction transaction) =>
        (transaction.AuthorizationList?.Length ?? 0) * GasCostOf.NewAccount;
}
