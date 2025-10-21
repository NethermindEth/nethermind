// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;


public readonly record struct IntrinsicGas(long Standard, long FloorGas)
{
    public long MinimalGas { get; } = Math.Max(Standard, FloorGas);
    public static explicit operator long(IntrinsicGas gas) => gas.MinimalGas;
}

public static class IntrinsicGasCalculator
{
    public static IntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec)
    {
        var intrinsicGas = GasCostOf.Transaction
               + DataCost(transaction, releaseSpec)
               + CreateCost(transaction, releaseSpec)
               + AccessListCost(transaction, releaseSpec)
               + AuthorizationListCost(transaction, releaseSpec);
        var floorGas = CalculateFloorCost(transaction, releaseSpec);
        return new IntrinsicGas(intrinsicGas, floorGas);
    }

    private static long CreateCost(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.IsContractCreation && releaseSpec.IsEip2Enabled ? GasCostOf.TxCreate : 0;

    private static long DataCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long baseDataCost = transaction.IsContractCreation && releaseSpec.IsEip3860Enabled
            ? EvmCalculations.Div32Ceiling((UInt256)transaction.Data.Length) *
              GasCostOf.InitCodeWord
            : 0;

        long tokensInCallData = CalculateTokensInCallData(transaction, releaseSpec);

        return baseDataCost + tokensInCallData * GasCostOf.TxDataZero;
    }

    public static long AccessListCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        AccessList? accessList = transaction.AccessList;
        if (accessList is not null)
        {
            if (!releaseSpec.UseTxAccessLists)
            {
                ThrowInvalidDataException(releaseSpec);
            }

            (int addressesCount, int storageKeysCount) = accessList.Count;
            return addressesCount * GasCostOf.AccessAccountListEntry + storageKeysCount * GasCostOf.AccessStorageListEntry;
        }

        return 0;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidDataException(IReleaseSpec releaseSpec)
        {
            throw new InvalidDataException($"Transaction with an access list received within the context of {releaseSpec.Name}. EIP-2930 is not enabled.");
        }
    }

    private static long AuthorizationListCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        AuthorizationTuple[]? transactionAuthorizationList = transaction.AuthorizationList;

        if (transactionAuthorizationList is not null)
        {
            if (!releaseSpec.IsAuthorizationListEnabled)
            {
                ThrowInvalidDataException(releaseSpec);
            }

            return transactionAuthorizationList.Length * GasCostOf.NewAccount;
        }

        return 0;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidDataException(IReleaseSpec releaseSpec)
        {
            throw new InvalidDataException($"Transaction with an authorization list received within the context of {releaseSpec.Name}. EIP-7702 is not enabled.");
        }
    }

    private static long CalculateTokensInCallData(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long txDataNonZeroMultiplier = releaseSpec.IsEip2028Enabled
            ? GasCostOf.TxDataNonZeroMultiplierEip2028
            : GasCostOf.TxDataNonZeroMultiplier;
        ReadOnlySpan<byte> data = transaction.Data.Span;

        int totalZeros = data.CountZeros();

        return totalZeros + (data.Length - totalZeros) * txDataNonZeroMultiplier;
    }

    private static long CalculateFloorCost(Transaction transaction, IReleaseSpec releaseSpec)
    {
        if (!releaseSpec.IsEip7623Enabled) return 0;
        long tokensInCallData = CalculateTokensInCallData(transaction, releaseSpec);

        return GasCostOf.Transaction + tokensInCallData * GasCostOf.TotalCostFloorPerTokenEip7623;
    }
}
