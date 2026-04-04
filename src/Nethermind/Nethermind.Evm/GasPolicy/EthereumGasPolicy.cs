// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Unified Ethereum gas policy supporting both legacy single-dimensional behavior and EIP-8037
/// two-dimensional behavior when opcode dispatch and spec flags enable it.
/// </summary>
public struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy>
{
    /// <summary>Regular gas budget (legacy gas_left).</summary>
    public long Value;
    /// <summary>State gas reservoir used by EIP-8037 paths.</summary>
    public long StateReservoir;
    /// <summary>Cumulative state gas used for block accounting.</summary>
    public long StateGasUsed;
    /// <summary>State gas that spilled from gas_left (for block regular gas exclusion).</summary>
    public long StateGasSpill;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy FromLong(long value) => new() { Value = value };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetRemainingGas(in EthereumGasPolicy gas) => gas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateReservoir(in EthereumGasPolicy gas) => gas.StateReservoir;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasUsed(in EthereumGasPolicy gas) => gas.StateGasUsed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasSpill(in EthereumGasPolicy gas) => gas.StateGasSpill;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Consume(ref EthereumGasPolicy gas, long cost) => gas.Value -= cost;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStateGas(ref EthereumGasPolicy gas, long stateGasCost)
    {
        if (gas.StateReservoir >= stateGasCost)
        {
            gas.StateReservoir -= stateGasCost;
            gas.StateGasUsed += stateGasCost;
            return true;
        }

        long spillAmount = stateGasCost - gas.StateReservoir;
        gas.StateReservoir = 0;
        if (!UpdateGas(ref gas, spillAmount))
        {
            return false;
        }

        gas.StateGasUsed += stateGasCost;
        gas.StateGasSpill += spillAmount;
        return true;
    }

    public static bool TryConsumeStateAndRegularGas(ref EthereumGasPolicy gas, long stateGasCost, long regularGasCost) =>
        (regularGasCost <= 0 || UpdateGas(ref gas, regularGasCost)) &&
        (stateGasCost <= 0 || ConsumeStateGas(ref gas, stateGasCost));

    public static bool ConsumeSelfDestructGas(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.SelfDestructEip150);

    /// <summary>
    /// Consume gas for code deposit. For standard Ethereum, this is equivalent to Consume.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeCodeDeposit(ref EthereumGasPolicy gas, long cost)
        => Consume(ref gas, cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Refund(ref EthereumGasPolicy gas, in EthereumGasPolicy childGas)
    {
        gas.Value += childGas.Value;
        gas.StateReservoir += childGas.StateReservoir;
        gas.StateGasUsed += childGas.StateGasUsed;
        gas.StateGasSpill += childGas.StateGasSpill;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGas(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas, long initialStateReservoir)
    {
        // On halt/revert, restore the full initial reservoir that was transferred to the child.
        // Also restore the spill amount: state gas that spilled from gas_left is returned to the
        // reservoir because the child's state operations are being reverted.
        parentGas.StateReservoir += initialStateReservoir + childGas.StateGasSpill;
        // Propagate spills — gas_left was consumed for state ops even though they're being reverted.
        parentGas.StateGasSpill += childGas.StateGasSpill;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RevertRefundToHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas, long initialStateReservoir)
    {
        // Code deposit failure is an exceptional halt: the child's state is reverted.
        // Refund already added child.StateReservoir, child.StateGasUsed, and child.StateGasSpill to parent.
        // Halt semantics require restoring the full initial reservoir (plus spill) and discarding child's StateGasUsed.
        // The spill is added to the reservoir because the child's state ops are being reverted.
        parentGas.StateReservoir += initialStateReservoir + childGas.StateGasSpill - childGas.StateReservoir;
        parentGas.StateGasUsed -= childGas.StateGasUsed;
    }

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
        return notOutOfGas && (delegated is null || ConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, delegated, chargeForWarm));
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
                accessTracker.WarmUp(address);
            }

            if (!spec.IsPrecompile(address) && accessTracker.WarmUp(address))
            {
                result = UpdateGas(ref gas, GasCostOf.ColdAccountAccess);
            }
            else if (chargeForWarm)
            {
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
        if (!spec.UseHotAndColdStorage)
            return true;
        if (isTracingAccess)
        {
            accessTracker.WarmUp(in storageCell);
        }

        if (accessTracker.WarmUp(in storageCell))
            return UpdateGas(ref gas, GasCostOf.ColdSLoad);
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
        if (GetRemainingGas(in gas) < gasCost)
            return false;

        Consume(ref gas, gasCost);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref EthereumGasPolicy gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
    {
        if (!TIsSlotCreation.IsActive) return UpdateGas(ref gas, spec.GasCosts.SStoreResetCost);
        return TEip8037.IsActive switch
        {
            // EIP-8037: charge the regular component first so an OOG halt does not
            // spill state gas into gas_left and then restore it to the parent frame.
            true => TryConsumeStateAndRegularGas(ref gas, GasCostOf.SSetState, GasCostOf.SSetRegular),
            false => UpdateGas(ref gas, GasCostOf.SSet),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref EthereumGasPolicy gas,
        long refund)
    {
        gas.Value += refund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor)
    {
        gas.StateReservoir += amount;
        long newFloor = Math.Max(0, stateGasFloor - amount);
        gas.StateGasUsed = Math.Max(gas.StateGasUsed - amount, newFloor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCodeInsertRegularRefund(int codeInsertRefunds, IReleaseSpec spec) =>
        spec.IsEip8037Enabled || codeInsertRefunds <= 0
            ? 0
            : (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ApplyCodeInsertRefunds(ref EthereumGasPolicy gas, int codeInsertRefunds, IReleaseSpec spec, long stateGasFloor)
    {
        if (codeInsertRefunds > 0 && spec.IsEip8037Enabled)
            RefundStateGas(ref gas, GasCostOf.NewAccountState * codeInsertRefunds, stateGasFloor);
        return GetCodeInsertRegularRefund(codeInsertRefunds, spec);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeCallValueTransfer(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.CallValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeNewAccountCreation<TEip8037>(ref EthereumGasPolicy gas) where TEip8037 : struct, IFlag
    {
        return TEip8037.IsActive switch
        {
            true => ConsumeStateGas(ref gas, GasCostOf.NewAccountState),
            false => UpdateGas(ref gas, GasCostOf.NewAccount)
        };
    }

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
    public static void OnBeforeInstructionTrace(in EthereumGasPolicy gas, int pc, Instruction instruction, int depth) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnAfterInstructionTrace(in EthereumGasPolicy gas) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy Max(in EthereumGasPolicy a, in EthereumGasPolicy b) =>
        a.Value >= b.Value ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateChildFrameGas(ref EthereumGasPolicy parentGas, long childRegularGas)
    {
        long childStateReservoir = parentGas.StateReservoir;
        parentGas.StateReservoir = 0;

        return new EthereumGasPolicy
        {
            Value = childRegularGas,
            StateReservoir = childStateReservoir,
            StateGasUsed = 0,
            StateGasSpill = 0,
        };
    }

    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec)
    {
        long tokensInCallData = IGasPolicy<EthereumGasPolicy>.CalculateTokensInCallData(tx, spec);
        (long authRegularCost, long authStateCost) = IGasPolicy<EthereumGasPolicy>.AuthorizationListCost(tx, spec);

        long regularGas = GasCostOf.Transaction
                          + DataCost(tx, spec, tokensInCallData)
                          + CreateCost(tx, spec)
                          + IGasPolicy<EthereumGasPolicy>.AccessListCost(tx, spec)
                          + authRegularCost;
        long floorCost = IGasPolicy<EthereumGasPolicy>.CalculateFloorCost(tx, spec, tokensInCallData);
        long createStateCost = CreateStateCost(tx, spec);
        long totalStateCost = authStateCost + createStateCost;
        return spec.IsEip8037Enabled
            ? new IntrinsicGas<EthereumGasPolicy>(
                new EthereumGasPolicy
                {
                    Value = regularGas,
                    StateReservoir = totalStateCost,
                    StateGasUsed = totalStateCost,
                },
                FromLong(floorCost))
            : new IntrinsicGas<EthereumGasPolicy>(FromLong(regularGas), FromLong(floorCost));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateAvailableFromIntrinsic(long gasLimit, in EthereumGasPolicy intrinsicGas, IReleaseSpec spec)
    {
        long executionGas = gasLimit - intrinsicGas.Value - intrinsicGas.StateReservoir;
        long reservoir = 0;

        if (spec.IsEip8037Enabled)
        {
            // EIP-8037: cap gas_left at TX_MAX_GAS_LIMIT - intrinsic_regular, overflow goes to reservoir
            long maxGasLeft = Eip7825Constants.DefaultTxGasLimitCap - intrinsicGas.Value;
            reservoir = Math.Max(0, executionGas - maxGasLeft);
            executionGas -= reservoir;
        }

        return new EthereumGasPolicy
        {
            Value = executionGas,
            StateReservoir = reservoir,
            StateGasUsed = intrinsicGas.StateReservoir,
            StateGasSpill = 0,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CreateCost(Transaction tx, IReleaseSpec spec) =>
        tx.IsContractCreation && spec.IsEip2Enabled
            ? (spec.IsEip8037Enabled ? GasCostOf.CreateRegular : GasCostOf.TxCreate)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CreateStateCost(Transaction tx, IReleaseSpec spec) =>
        tx.IsContractCreation && spec.IsEip8037Enabled ? GasCostOf.CreateState : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DataCost(Transaction tx, IReleaseSpec spec, long tokensInCallData) =>
        spec.GetBaseDataCost(tx) + tokensInCallData * GasCostOf.TxDataZero;

}
