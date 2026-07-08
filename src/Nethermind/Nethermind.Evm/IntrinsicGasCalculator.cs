// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

namespace Nethermind.Evm;

/// <summary>
/// Non-generic intrinsic gas result for backward compatibility.
/// </summary>
public readonly record struct EthereumIntrinsicGas(ulong Standard, ulong FloorGas)
{
    public ulong MinimalGas { get; } = Math.Max(Standard, FloorGas);
    public static explicit operator ulong(EthereumIntrinsicGas gas) => gas.MinimalGas;
    public static implicit operator EthereumIntrinsicGas(IntrinsicGas<EthereumGasPolicy> gas) =>
        new(gas.Standard.Value + gas.Standard.StateReservoir, gas.FloorGas.Value);
}

public static class IntrinsicGasCalculator
{
    /// <summary>
    /// Calculates intrinsic gas with TGasPolicy type, allowing MultiGas breakdown for Arbitrum.
    /// </summary>
    private static IntrinsicGas<TGasPolicy> Calculate<TGasPolicy>(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit = 0)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.CalculateIntrinsicGas(transaction, releaseSpec, blockGasLimit);

    /// <summary>
    /// Non-generic backward-compatible Calculate method.
    /// </summary>
    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit = 0) =>
        Calculate<EthereumGasPolicy>(transaction, releaseSpec, blockGasLimit);

    public static ulong AccessListCost(Transaction transaction, IReleaseSpec releaseSpec) =>
        AccessListCost(transaction, releaseSpec, CalculateFloorTokensInAccessList(transaction, releaseSpec));

    internal static ulong CalculateTokensInCallData(Transaction transaction, IReleaseSpec spec)
    {
        ReadOnlySpan<byte> data = transaction.Data.Span;
        int totalZeros = data.CountZeros();
        return (ulong)totalZeros + (ulong)(data.Length - totalZeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }

    // 0 when floor pricing is not active.
    internal static ulong CalculateFloorTokensInAccessList(Transaction transaction, IReleaseSpec spec) =>
        spec.IsEip7981Enabled && transaction.AccessList is { Count: (int addressesCount, int storageKeysCount) }
            ? (ulong)(addressesCount * Address.Size + storageKeysCount * AccessList.StorageKeySize) * spec.GasCosts.TxDataNonZeroMultiplier
            : 0;

    internal static ulong AccessListCost(Transaction transaction, IReleaseSpec spec, ulong floorTokensInAccessList)
    {
        AccessList? accessList = transaction.AccessList;
        if (accessList is null) return 0;

        if (!spec.UseTxAccessLists)
        {
            ThrowInvalidDataException(spec);
        }

        (int addressesCount, int storageKeysCount) = accessList.Count;
        return (ulong)addressesCount * GasCostOf.AccessAccountListEntry
            + (ulong)storageKeysCount * GasCostOf.AccessStorageListEntry
            + spec.GasCosts.TotalCostFloorPerToken * floorTokensInAccessList;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidDataException(IReleaseSpec spec) =>
            throw new InvalidDataException($"Transaction with an access list received within the context of {spec.Name}. EIP-2930 is not enabled.");
    }

    internal static (ulong RegularCost, ulong StateCost) AuthorizationListCost(Transaction transaction, IReleaseSpec spec)
    {
        AuthorizationTuple[]? authList = transaction.AuthorizationList;
        if (authList is null)
        {
            return (0, 0);
        }

        if (!spec.IsAuthorizationListEnabled)
        {
            ThrowAuthorizationListNotEnabled(spec);
        }

        ulong authCount = (ulong)authList.Length;
        return spec.IsEip8037Enabled
            ? (
                authCount * GasCostOf.PerAuthBaseRegular,
                authCount * (GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState)
            )
            : (authCount * GasCostOf.NewAccount, 0);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowAuthorizationListNotEnabled(IReleaseSpec releaseSpec) =>
            throw new InvalidDataException($"Transaction with an authorization list received within the context of {releaseSpec.Name}. EIP-7702 is not enabled.");
    }

    private static ulong CalculateFloorTokensInCallData(Transaction transaction, IReleaseSpec spec) =>
        (ulong)transaction.Data.Length * spec.GasCosts.TxDataNonZeroMultiplier;

    /// <summary>
    /// We should use the post-EIP-2028 non-zero calldata multiplier for the EIP-7623 floor cost,
    /// even when EIP-2028 itself is disabled and the regular intrinsic calldata cost still uses
    /// the pre-EIP-2028 pricing.
    /// Matches Geth behavior.
    /// </summary>
    private static ulong CalculateEip7623FloorTokensInCallData(Transaction transaction, IReleaseSpec spec, ulong tokensInCallData)
    {
        if (spec.IsEip2028Enabled) return tokensInCallData;

        ReadOnlySpan<byte> data = transaction.Data.Span;
        ulong totalZeros = (ulong)data.CountZeros();
        return totalZeros + ((ulong)data.Length - totalZeros) * GasCostOf.TxDataNonZeroMultiplierEip2028;
    }

    internal static ulong CalculateFloorCost(Transaction transaction, IReleaseSpec spec, ulong tokensInCallData, ulong floorTokensInAccessList) => spec switch
    {
        { IsEip7976Enabled: true } => GasCostOf.Transaction + (CalculateFloorTokensInCallData(transaction, spec) + floorTokensInAccessList) * spec.GasCosts.TotalCostFloorPerToken,
        { IsEip7623Enabled: true } => GasCostOf.Transaction + CalculateEip7623FloorTokensInCallData(transaction, spec, tokensInCallData) * spec.GasCosts.TotalCostFloorPerToken,
        _ => 0
    };
}
