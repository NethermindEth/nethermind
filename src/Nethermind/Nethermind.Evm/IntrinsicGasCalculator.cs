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
    public static explicit operator TGasPolicy(IntrinsicGas<TGasPolicy> gas) => gas.MinimalGas;
}

/// <summary>
/// Non-generic intrinsic gas result for backward compatibility.
/// </summary>
public readonly record struct EthereumIntrinsicGas(long Standard, long FloorGas)
{
    public long MinimalGas { get; } = Math.Max(Standard, FloorGas);
    public static explicit operator long(EthereumIntrinsicGas gas) => gas.MinimalGas;
}

/// <summary>
/// Calculates intrinsic gas for transactions with caching calculations.
/// Caches Standard and FloorGas values to eliminate recalculations on cache hits.
/// </summary>
public static class IntrinsicGasCalculator
{
    /// <summary>
    /// Calculates intrinsic gas with TGasPolicy type, allowing MultiGas breakdown for Arbitrum
    /// Common calculation logic for Ethereum intrinsic gas
    /// Eth policy - Standard gas only as it stands
    /// </summary>
    internal static long CalculateStandardGas(Transaction transaction, IReleaseSpec releaseSpec)
    {
        return GasCostOf.Transaction
               + DataCost(transaction, releaseSpec)
               + CreateCost(transaction, releaseSpec)
               + AccessListCost(transaction, releaseSpec)
               + AuthorizationListCost(transaction, releaseSpec);
    }

    /// <summary>
    /// Calculates intrinsic gas with TGasPolicy type.
    /// Uses cached values if available to skip recalculation
    /// When no cache exists, calculates and caches StandardGas and FloorGas.
    /// </summary>
    public static IntrinsicGas<TGasPolicy> Calculate<TGasPolicy>(Transaction transaction, IReleaseSpec releaseSpec)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (transaction.CachedIntrinsicGas.HasValue)
        {
            var (StandardGas, FloorGas) = transaction.CachedIntrinsicGas.Value;
            
            TGasPolicy cachedStandardGasPolicy = TGasPolicy.FromLong(StandardGas);
            TGasPolicy cachedFloorGasPolicy = TGasPolicy.FromLong(FloorGas);
            
            return new IntrinsicGas<TGasPolicy>(cachedStandardGasPolicy, cachedFloorGasPolicy);
        }

        long calculatedStandardGas = CalculateStandardGas(transaction, releaseSpec);
        long calculatedFloorGas = CalculateFloorCost(transaction, releaseSpec);

        TGasPolicy standardGasPolicy = TGasPolicy.FromLong(calculatedStandardGas);
        TGasPolicy floorGasPolicy = TGasPolicy.FromLong(calculatedFloorGas);
                
        transaction.CachedIntrinsicGas = (calculatedStandardGas, calculatedFloorGas);
        
        return new IntrinsicGas<TGasPolicy>(standardGasPolicy, floorGasPolicy);
    }

    /// <summary>
    /// Non-generic backward-compatible Calculate method.
    /// Calculates and caches StandardGas and FloorGas values.
    /// </summary>
    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec)
    {
        long StandardGas = CalculateStandardGas(transaction, releaseSpec);
        long FloorGas = CalculateFloorCost(transaction, releaseSpec);
        
        var result = new EthereumIntrinsicGas(StandardGas, FloorGas);
        
        transaction.CachedIntrinsicGas = (StandardGas, FloorGas);

        return result;
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
