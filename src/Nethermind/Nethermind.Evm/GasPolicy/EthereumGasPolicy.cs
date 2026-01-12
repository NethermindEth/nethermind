// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Standard Ethereum single-dimensional gas.Value policy.
/// </summary>
public struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy>
{
    public long Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy FromLong(long value) => new() { Value = value };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetRemainingGas(in EthereumGasPolicy gas) => gas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Consume(ref EthereumGasPolicy gas, long cost) =>
        gas.Value -= cost;

    public static void ConsumeSelfDestructGas(ref EthereumGasPolicy gas)
        => Consume(ref gas, GasCostOf.SelfDestructEip150);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Refund(ref EthereumGasPolicy gas, in EthereumGasPolicy childGas) =>
        gas.Value += childGas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref EthereumGasPolicy gas) => gas.Value = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeAccountAccessGasWithDelegation(ref EthereumGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        Address? delegated,
        bool chargeForWarm = true)
    {
        if (!spec.UseHotAndColdStorage)
            return true;

        bool notOutOfGas = ConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, address, chargeForWarm);
        return notOutOfGas
               && (delegated is null
                   || ConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, delegated, chargeForWarm));
    }

    public static bool ConsumeAccountAccessGas(ref EthereumGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        bool chargeForWarm = true)
    {
        bool result = true;
        if (spec.UseHotAndColdStorage)
        {
            if (isTracingAccess)
            {
                // Ensure that tracing simulates access-list behavior.
                accessTracker.WarmUp(address);
            }

            // If the account is cold (and not a precompile), charge the cold access cost.
            if (!spec.IsPrecompile(address) && accessTracker.WarmUp(address))
            {
                result = UpdateGas(ref gas, GasCostOf.ColdAccountAccess);
            }
            else if (chargeForWarm)
            {
                // Otherwise, if warm access should be charged, apply the warm read cost.
                result = UpdateGas(ref gas, GasCostOf.WarmStateRead);
            }
        }

        return result;
    }

    public static bool ConsumeStorageAccessGas(ref EthereumGasPolicy gas,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        // If the spec requires hot/cold storage tracking, determine if extra gas should be charged.
        if (!spec.UseHotAndColdStorage)
            return true;
        // When tracing access, ensure the storage cell is marked as warm to simulate inclusion in the access list.
        if (isTracingAccess)
        {
            accessTracker.WarmUp(in storageCell);
        }

        // If the storage cell is still cold, apply the higher cold access cost and mark it as warm.
        if (accessTracker.WarmUp(in storageCell))
            return UpdateGas(ref gas, GasCostOf.ColdSLoad);
        // For SLOAD operations on already warmed-up storage, apply a lower warm-read cost.
        if (storageAccessType == StorageAccessType.SLOAD)
            return UpdateGas(ref gas, GasCostOf.WarmStateRead);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref EthereumGasPolicy gas,
        in UInt256 position,
        in UInt256 length, VmState<EthereumGasPolicy> vmState)
    {
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost == 0L)
            return !outOfGas;
        return UpdateGas(ref gas, memoryCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(ref EthereumGasPolicy gas,
        long gasCost)
    {
        if (GetRemainingGas(gas) < gasCost)
            return false;

        Consume(ref gas, gasCost);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref EthereumGasPolicy gas,
        long refund)
    {
        gas.Value += refund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStorageWrite(ref EthereumGasPolicy gas, bool isSlotCreation, IReleaseSpec spec)
    {
        long cost = isSlotCreation ? GasCostOf.SSet : spec.GetSStoreResetCost();
        return UpdateGas(ref gas, cost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeCallValueTransfer(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.CallValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeNewAccountCreation(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.NewAccount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeLogEmission(ref EthereumGasPolicy gas, long topicCount, long dataSize)
    {
        long cost = GasCostOf.Log + topicCount * GasCostOf.LogTopic + dataSize * GasCostOf.LogData;
        return UpdateGas(ref gas, cost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeDataCopyGas(ref EthereumGasPolicy gas, bool isExternalCode, long baseCost, long dataCost)
        => Consume(ref gas, baseCost + dataCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy Max(in EthereumGasPolicy a, in EthereumGasPolicy b) =>
        a.Value >= b.Value ? a : b;

    public static EthereumGasPolicy CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec)
    {
        long gas = GasCostOf.Transaction
            + DataCost(tx, spec)
            + CreateCost(tx, spec)
            + IntrinsicGasCalculator.AccessListCost(tx, spec)
            + AuthorizationListCost(tx, spec);
        return new() { Value = gas };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateAvailableFromIntrinsic(long gasLimit, in EthereumGasPolicy intrinsicGas)
        => new() { Value = gasLimit - intrinsicGas.Value };

    private static long CreateCost(Transaction tx, IReleaseSpec spec) =>
        tx.IsContractCreation && spec.IsEip2Enabled ? GasCostOf.TxCreate : 0;

    private static long DataCost(Transaction tx, IReleaseSpec spec)
    {
        long baseDataCost = tx.IsContractCreation && spec.IsEip3860Enabled
            ? EvmCalculations.Div32Ceiling((UInt256)tx.Data.Length) * GasCostOf.InitCodeWord
            : 0;

        long tokensInCallData = CalculateTokensInCallData(tx, spec);
        return baseDataCost + tokensInCallData * GasCostOf.TxDataZero;
    }

    private static long CalculateTokensInCallData(Transaction tx, IReleaseSpec spec)
    {
        long txDataNonZeroMultiplier = spec.IsEip2028Enabled
            ? GasCostOf.TxDataNonZeroMultiplierEip2028
            : GasCostOf.TxDataNonZeroMultiplier;
        ReadOnlySpan<byte> data = tx.Data.Span;
        int totalZeros = data.CountZeros();
        return totalZeros + (data.Length - totalZeros) * txDataNonZeroMultiplier;
    }

    private static long AuthorizationListCost(Transaction tx, IReleaseSpec spec)
    {
        AuthorizationTuple[]? authList = tx.AuthorizationList;
        if (authList is not null)
        {
            if (!spec.IsAuthorizationListEnabled)
                ThrowAuthorizationListNotEnabled(spec);
            return authList.Length * GasCostOf.NewAccount;
        }
        return 0;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAuthorizationListNotEnabled(IReleaseSpec spec) =>
        throw new InvalidDataException($"Transaction with an authorization list received within the context of {spec.Name}. EIP-7702 is not enabled.");
}
