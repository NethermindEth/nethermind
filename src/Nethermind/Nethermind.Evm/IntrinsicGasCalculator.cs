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
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Generic intrinsic gas result with TGasPolicy-typed Standard and FloorGas.
/// </summary>
public readonly record struct IntrinsicGas<TGasPolicy>(TGasPolicy Standard, TGasPolicy FloorGas)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public TGasPolicy MinimalGas { get; } = TGasPolicy.Max(Standard, FloorGas);
    public static explicit operator TGasPolicy(in IntrinsicGas<TGasPolicy> gas) => gas.MinimalGas;
}

/// <summary>
/// Non-generic intrinsic gas result for backward compatibility.
/// </summary>
public readonly record struct EthereumIntrinsicGas(long Standard, long FloorGas)
{
    public long MinimalGas { get; } = Math.Max(Standard, FloorGas);
    public static explicit operator long(EthereumIntrinsicGas gas) => gas.MinimalGas;
}

public static class IntrinsicGasCalculator
{
    /// <summary>
    /// Calculates intrinsic gas with TGasPolicy type, allowing MultiGas breakdown for Arbitrum.
    /// </summary>
    public static IntrinsicGas<TGasPolicy> Calculate<TGasPolicy>(Transaction transaction, IReleaseSpec releaseSpec)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // Compute tokensInCallData once - CountZeros uses SIMD
        long tokensInCallData = CalculateTokensInCallData(transaction, releaseSpec);
        TGasPolicy standard = TGasPolicy.CalculateIntrinsicGas(transaction, releaseSpec, tokensInCallData);
        long floorCost = CalculateFloorCost(tokensInCallData, releaseSpec);
        TGasPolicy floorGas = TGasPolicy.FromLong(floorCost);
        return new IntrinsicGas<TGasPolicy>(standard, floorGas);
    }

    /// <summary>
    /// Non-generic backward-compatible Calculate method.
    /// </summary>
    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec)
    {
        // Compute tokensInCallData once - CountZeros uses SIMD
        long tokensInCallData = CalculateTokensInCallData(transaction, releaseSpec);
        long intrinsicGas = GasCostOf.Transaction
               + DataCost(transaction, releaseSpec, tokensInCallData)
               + CreateCost(transaction, releaseSpec)
               + AccessListCost(transaction, releaseSpec)
               + AuthorizationListCost(transaction, releaseSpec);
        long floorGas = CalculateFloorCost(tokensInCallData, releaseSpec);
        return new EthereumIntrinsicGas(intrinsicGas, floorGas);
    }

    private static long CreateCost(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.IsContractCreation && releaseSpec.IsEip2Enabled ? GasCostOf.TxCreate : 0;

    private static long DataCost(Transaction transaction, IReleaseSpec releaseSpec, long tokensInCallData)
    {
        long baseDataCost = transaction.IsContractCreation && releaseSpec.IsEip3860Enabled
            ? EvmCalculations.Div32Ceiling((UInt256)transaction.Data.Length) *
              GasCostOf.InitCodeWord
            : 0;

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

    public static long CalculateTokensInCallData(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long txDataNonZeroMultiplier = releaseSpec.IsEip2028Enabled
            ? GasCostOf.TxDataNonZeroMultiplierEip2028
            : GasCostOf.TxDataNonZeroMultiplier;
        ReadOnlySpan<byte> data = transaction.Data.Span;

        int totalZeros = data.CountZeros();

        return totalZeros + (data.Length - totalZeros) * txDataNonZeroMultiplier;
    }

    private static long CalculateFloorCost(long tokensInCallData, IReleaseSpec releaseSpec)
    {
        if (!releaseSpec.IsEip7623Enabled) return 0;
        return GasCostOf.Transaction + tokensInCallData * GasCostOf.TotalCostFloorPerTokenEip7623;
    }
}
