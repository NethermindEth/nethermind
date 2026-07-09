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
        new(gas.Standard.Value + (ulong)gas.Standard.StateReservoir, gas.FloorGas.Value);
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
        // EIP-8038 realigns access-list entry costs with the cold-access costs they pre-warm.
        ulong addressCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListAddressCost : GasCostOf.AccessAccountListEntry;
        ulong storageKeyCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListStorageKeyCost : GasCostOf.AccessStorageListEntry;
        return (ulong)addressesCount * addressCost
            + (ulong)storageKeysCount * storageKeyCost
            + spec.GasCosts.TotalCostFloorPerToken * floorTokensInAccessList;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidDataException(IReleaseSpec spec) =>
            throw new InvalidDataException($"Transaction with an access list received within the context of {spec.Name}. EIP-2930 is not enabled.");
    }

    internal static (ulong RegularCost, long StateCost) AuthorizationListCost(Transaction transaction, IReleaseSpec spec)
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
        ulong perAuthRegular = spec.IsEip8038Enabled ? Eip8038Constants.PerAuthBaseRegular : GasCostOf.PerAuthBaseRegular;
        return spec.IsEip8037Enabled
            ? (
                authCount * perAuthRegular,
                authList.Length * (GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState)
            )
            : (authCount * GasCostOf.NewAccount, 0);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowAuthorizationListNotEnabled(IReleaseSpec releaseSpec) =>
            throw new InvalidDataException($"Transaction with an authorization list received within the context of {releaseSpec.Name}. EIP-7702 is not enabled.");
    }

    private static ulong CalculateFloorTokensInCallData(Transaction transaction, IReleaseSpec spec) =>
        (ulong)transaction.Data.Length * spec.GasCosts.TxDataNonZeroMultiplier;

    internal static ulong CalculateFloorCost(Transaction transaction, IReleaseSpec spec, ulong tokensInCallData, ulong floorTokensInAccessList)
    {
        // The floor tracks the reduced EIP-2780 base, else the legacy floor would dominate.
        ulong floorBase = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        return spec switch
        {
            { IsEip7976Enabled: true } => floorBase + (CalculateFloorTokensInCallData(transaction, spec) + floorTokensInAccessList) * spec.GasCosts.TotalCostFloorPerToken,
            { IsEip7623Enabled: true } => floorBase + tokensInCallData * spec.GasCosts.TotalCostFloorPerToken,
            _ => 0
        };
    }
}
