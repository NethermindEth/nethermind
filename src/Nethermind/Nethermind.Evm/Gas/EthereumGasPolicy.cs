// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.Gas;

/// <summary>
/// Standard Ethereum single-dimensional gas policy.
/// </summary>
public readonly struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetRemainingGas(in GasState<EthereumGasPolicy> gasState) => gasState.RemainingGas;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeGas(ref GasState<EthereumGasPolicy> gasState, long gasCost) =>
        gasState.RemainingGas -= gasCost;

    public static void ChargeSelfDestructGas(ref GasState<EthereumGasPolicy> gasState)
        => ConsumeGas(ref gasState, GasCostOf.SelfDestructEip150);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundGas(ref GasState<EthereumGasPolicy> gasState, long gasAmount) =>
        gasState.RemainingGas += gasAmount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref GasState<EthereumGasPolicy> gasState) => gasState.RemainingGas = 0;

    public static bool ChargeAccountAccessGasWithDelegation(
        ref GasState<EthereumGasPolicy> gasState,
        VirtualMachine<EthereumGasPolicy> vm,
        Address address,
        bool chargeForWarm = true)
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.UseHotAndColdStorage)
            return true;

        bool notOutOfGas = ChargeAccountAccessGas(ref gasState, vm, address, chargeForWarm);
        return notOutOfGas
               && (!vm.TxExecutionContext.CodeInfoRepository.TryGetDelegation(address, spec, out Address delegated)
                   || ChargeAccountAccessGas(ref gasState, vm, delegated, chargeForWarm));
    }

    public static bool ChargeAccountAccessGas(
        ref GasState<EthereumGasPolicy> gasState,
        VirtualMachine<EthereumGasPolicy> vm,
        Address address,
        bool chargeForWarm = true)
    {
        bool result = true;
        IReleaseSpec spec = vm.Spec;
        if (spec.UseHotAndColdStorage)
        {
            EvmState vmState = vm.EvmState;
            if (vm.TxTracer.IsTracingAccess)
                vmState.AccessTracker.WarmUp(address);

            if (!spec.IsPrecompile(address) && vmState.AccessTracker.WarmUp(address))
                result = UpdateGas(ref gasState, GasCostOf.ColdAccountAccess);
            else if (chargeForWarm)
                result = UpdateGas(ref gasState, GasCostOf.WarmStateRead);
        }

        return result;
    }

    public static bool ChargeStorageAccessGas(
        ref GasState<EthereumGasPolicy> gasState,
        VirtualMachine<EthereumGasPolicy> vm,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        EvmState vmState = vm.EvmState;
        bool result = true;

        if (spec.UseHotAndColdStorage)
        {
            ref readonly StackAccessTracker accessTracker = ref vmState.AccessTracker;
            if (vm.TxTracer.IsTracingAccess)
                accessTracker.WarmUp(in storageCell);

            if (accessTracker.WarmUp(in storageCell))
                result = UpdateGas(ref gasState, GasCostOf.ColdSLoad);
            else if (storageAccessType == StorageAccessType.SLOAD)
                result = UpdateGas(ref gasState, GasCostOf.WarmStateRead);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref GasState<EthereumGasPolicy> gasState,
        in UInt256 position,
        in UInt256 length, EvmState vmState)
    {
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost != 0L)
        {
            if (!UpdateGas(ref gasState, memoryCost))
                return false;
        }

        return !outOfGas;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(
        ref GasState<EthereumGasPolicy> gasState,
        long gasCost)
    {
        if (GetRemainingGas(gasState) < gasCost)
            return false;

        ConsumeGas(ref gasState, gasCost);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(
        ref GasState<EthereumGasPolicy> gasState,
        long refund)
    {
        gasState.RemainingGas += refund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ChargeStorageWrite(
        ref GasState<EthereumGasPolicy> gasState,
        long cost,
        bool isSlotCreation) =>
        UpdateGas(ref gasState, cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ChargeCallExtra(
        ref GasState<EthereumGasPolicy> gasState,
        long cost,
        bool isNewAccount) =>
        UpdateGas(ref gasState, cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ChargeLogEmission(
        ref GasState<EthereumGasPolicy> gasState,
        long topicCount,
        long dataSize,
        long totalCost) =>
        UpdateGas(ref gasState, totalCost);
}
