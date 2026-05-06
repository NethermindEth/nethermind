// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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
    /// <summary>EIP-8037 / execution-specs PR 2703 tx-cumulative spill from reverted child frames used by top-level halt accounting.</summary>
    public long StateGasSpillBurned;
    /// <summary>EIP-8037 / execution-specs PR 2703 spill that should remain in the block regular dimension.</summary>
    public long StateGasSpillReclassified;
    /// <summary>EIP-8037 / execution-specs PR 2703 spill consumed by state refunds and excluded from block regular gas.</summary>
    public long StateGasSpillRefunded;
    /// <summary>Per-block EIP-8037 cost-per-state-byte.</summary>
    public long CostPerStateByte;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy FromLong(long value) => new() { Value = value, CostPerStateByte = GasCostOf.CostPerStateByte };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EthereumGasPolicy FromLong(long value, long costPerStateByte) => new() { Value = value, CostPerStateByte = costPerStateByte };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateSystemTransactionIntrinsicGas(long blockGasLimit) =>
        FromLong(0, GasCostOf.CalculateCostPerStateByte(blockGasLimit));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetRemainingGas(in EthereumGasPolicy gas) => gas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCostPerStateByte(in EthereumGasPolicy gas)
    {
        Debug.Assert(gas.CostPerStateByte >= 0, "CostPerStateByte must be explicitly initialized or intentionally set to zero.");
        return gas.CostPerStateByte;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStorageSetStateCost(in EthereumGasPolicy gas) => GasCostOf.CalculateSSetState(GetCostPerStateByte(in gas));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCreateStateCost(in EthereumGasPolicy gas) => GasCostOf.CalculateCreateState(GetCostPerStateByte(in gas));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetNewAccountStateCost(in EthereumGasPolicy gas) => GasCostOf.CalculateNewAccountState(GetCostPerStateByte(in gas));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetPerAuthBaseStateCost(in EthereumGasPolicy gas) => GasCostOf.CalculatePerAuthBaseState(GetCostPerStateByte(in gas));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCodeDepositStateCost(in EthereumGasPolicy gas, int byteCodeLength) => GasCostOf.CalculateCodeDepositState(GetCostPerStateByte(in gas), byteCodeLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStorageSetReversalRefund(in EthereumGasPolicy gas) => GasCostOf.CalculateSSetReversalRefund(GetCostPerStateByte(in gas));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateReservoir(in EthereumGasPolicy gas) => gas.StateReservoir;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasUsed(in EthereumGasPolicy gas) => gas.StateGasUsed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasSpill(in EthereumGasPolicy gas) => gas.StateGasSpill;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasSpillBurned(in EthereumGasPolicy gas) => gas.StateGasSpillBurned;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasSpillReclassified(in EthereumGasPolicy gas) => gas.StateGasSpillReclassified;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasSpillRefunded(in EthereumGasPolicy gas) => gas.StateGasSpillRefunded;


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
        if (!UpdateGas(ref gas, spillAmount))
        {
            return false;
        }

        gas.StateReservoir = 0;
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
        gas.StateGasSpillBurned += childGas.StateGasSpillBurned;
        gas.StateGasSpillReclassified += childGas.StateGasSpillReclassified;
        gas.StateGasSpillRefunded += childGas.StateGasSpillRefunded;
        Debug.Assert(gas.CostPerStateByte == childGas.CostPerStateByte, "CostPerStateByte must flow parent to child, not child to parent.");
    }

    // On explicit REVERT, restore the child's remaining state reservoir plus its unreverted
    // state gas usage, then subtract inline refunds that inflated the reservoir inside the
    // child. The child's StateGasSpill (which already includes any spill propagated up from
    // descendants) is recorded permanently in StateGasSpillBurned for top-level halt accounting.
    // State-gas refunds mark their spilled portion in StateGasSpillRefunded. If this REVERT
    // carries such a refund, any remaining unrefunded spill stays in the regular dimension.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGas(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas, long initialStateReservoir, long childStateRefund)
    {
        long unrefundedSpill = GetUnrefundedStateGasSpill(in childGas, childStateRefund);
        long reclassifiedSpill = childGas.StateGasSpillReclassified;
        if (childStateRefund > 0)
        {
            reclassifiedSpill += GetUnclassifiedStateGasSpill(in childGas, unrefundedSpill);
        }

        parentGas.StateReservoir += childGas.StateReservoir + childGas.StateGasUsed - childStateRefund;
        parentGas.StateGasSpill += childGas.StateGasSpill;
        parentGas.StateGasSpillBurned += unrefundedSpill;
        parentGas.StateGasSpillReclassified += reclassifiedSpill;
        parentGas.StateGasSpillRefunded += childGas.StateGasSpillRefunded;
    }

    // Do NOT propagate childGas.StateGasSpill into parentGas.StateGasSpill: an exceptional halt
    // burns the child's regular gas - including the portion that was charged via spill. Letting
    // Calculate8037BlockRegularGas subtract that already-burned gas from the block's regular total
    // would undercount block.gasUsed by the spill amount.
    // Do NOT add childGas.StateGasSpill to parentGas.StateGasSpillBurned either: an inner halt's
    // burned regular gas is already included in the initial regular budget that the top-level
    // halt formula attributes to the regular dimension. Adding it again via spillBurned would
    // double-count. Only propagate child's cumulative spillBurned,
    // which carries grand-descendant REVERT spill that hasn't yet been attributed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGasOnHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas, long initialStateReservoir)
    {
        parentGas.StateReservoir += GetRestoredChildStateReservoir(in childGas, initialStateReservoir);
        parentGas.StateGasSpillBurned += childGas.StateGasSpillBurned;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RevertRefundToHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas, long initialStateReservoir)
    {
        // Code deposit failure is an exceptional halt: the child's state is reverted.
        // Refund already added child.StateReservoir, child.StateGasUsed, child.StateGasSpill,
        // and child.StateGasSpillBurned to parent. Halt semantics discard child's StateGasUsed
        // and return it to the reservoir. Leave parent.StateGasSpill alone: its child-portion
        // is already accounted for by the non-halt block-regular formula's `- StateGasSpill`
        // adjustment, and rerouting into StateGasSpillBurned would double-attribute the same
        // gas at the block-level halt formula.
        parentGas.StateReservoir += GetRestoredChildStateReservoir(in childGas, initialStateReservoir) - childGas.StateReservoir;
        parentGas.StateGasUsed -= childGas.StateGasUsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetRestoredChildStateReservoir(in EthereumGasPolicy childGas, long initialStateReservoir)
    {
        if (childGas.StateReservoir >= initialStateReservoir)
            return initialStateReservoir;

        return childGas.StateReservoir + Math.Min(childGas.StateGasUsed, initialStateReservoir - childGas.StateReservoir);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetUnrefundedStateGasSpill(in EthereumGasPolicy childGas, long stateRefund = 0)
    {
        long refundedSpill = Math.Max(
            childGas.StateGasSpillRefunded,
            Math.Min(stateRefund, childGas.StateGasSpill));
        return Math.Max(0, childGas.StateGasSpill - refundedSpill);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetUnclassifiedStateGasSpill(in EthereumGasPolicy childGas, long unrefundedSpill) =>
        Math.Max(0, unrefundedSpill - childGas.StateGasSpillReclassified);

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
    public static bool UpdateMemoryCost(ref EthereumGasPolicy gas,
        in UInt256 position,
        ulong length, VmState<EthereumGasPolicy> vmState)
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
            true => TryConsumeStateAndRegularGas(ref gas, GetStorageSetStateCost(in gas), GasCostOf.SSetRegular),
            false => UpdateGas(ref gas, GasCostOf.SSet),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref EthereumGasPolicy gas,
        long refund) => gas.Value += refund;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor)
        => RefundStateGas(ref gas, amount, stateGasFloor, trackSpillRefund: true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor, bool trackSpillRefund)
    {
        long refundableStateGas = Math.Max(0, gas.StateGasUsed - stateGasFloor);
        long appliedRefund = Math.Min(amount, refundableStateGas);
        long unrefundedSpill = GetUnrefundedStateGasSpill(in gas);
        if (trackSpillRefund)
        {
            gas.StateGasSpillRefunded += Math.Min(appliedRefund, unrefundedSpill);
        }

        gas.StateReservoir += appliedRefund;
        gas.StateGasUsed -= appliedRefund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DiscardStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor)
    {
        long discardableStateGas = Math.Max(0, gas.StateGasUsed - stateGasFloor);
        gas.StateGasUsed -= Math.Min(amount, discardableStateGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ResetForHalt(ref EthereumGasPolicy gas, long initialStateReservoir, long initialStateGasUsed)
    {
        // Snap state-gas back to its tx-start shape (reservoir=R0, used=intrinsic floor,
        // spill=0). The post-reset StateGasUsed feeds SpentGas so the user does not keep
        // paying for state-gas that did not commit.
        // StateGasSpillBurned is intentionally preserved: it records spill from inner-frame
        // reverts that was burned earlier in the tx and must remain available to the halt
        // formula so the spill can be reattributed from state to regular dimension.
        gas.StateReservoir = initialStateReservoir;
        gas.StateGasUsed = initialStateGasUsed;
        gas.StateGasSpill = 0;
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
        {
            // EIP-8037 + EIP-7702: a successful authority-code insertion refunds its
            // state-gas allowance into the reservoir for later state work, but the
            // intrinsic state floor remains charged to block state accounting.
            gas.StateReservoir += checked(GetNewAccountStateCost(in gas) * codeInsertRefunds);
        }

        return GetCodeInsertRegularRefund(codeInsertRefunds, spec);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeCallValueTransfer(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.CallValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeNewAccountCreation<TEip8037>(ref EthereumGasPolicy gas) where TEip8037 : struct, IFlag => TEip8037.IsActive switch
    {
        true => ConsumeStateGas(ref gas, GetNewAccountStateCost(in gas)),
        false => UpdateGas(ref gas, GasCostOf.NewAccount)
    };

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
            CostPerStateByte = GetCostPerStateByte(in parentGas),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);

    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, long blockGasLimit)
    {
        long tokensInCallData = IGasPolicy<EthereumGasPolicy>.CalculateTokensInCallData(tx, spec);
        long costPerStateByte = GasCostOf.CalculateCostPerStateByte(blockGasLimit);
        long floorTokensInAccessList = IGasPolicy<EthereumGasPolicy>.CalculateFloorTokensInAccessList(tx, spec);
        (long authRegularCost, long authStateCost) = IGasPolicy<EthereumGasPolicy>.AuthorizationListCost(tx, spec, blockGasLimit);
        long accessListCost = IGasPolicy<EthereumGasPolicy>.AccessListCost(tx, spec, floorTokensInAccessList);

        long regularGas = GasCostOf.Transaction
                          + DataCost(tx, spec, tokensInCallData)
                          + CreateCost(tx, spec)
                          + accessListCost
                          + authRegularCost;
        long floorCost = IGasPolicy<EthereumGasPolicy>.CalculateFloorCost(tx, spec, tokensInCallData, floorTokensInAccessList);
        long createStateCost = CreateStateCost(tx, spec, costPerStateByte);
        long totalStateCost = authStateCost + createStateCost;
        return spec.IsEip8037Enabled
            ? new IntrinsicGas<EthereumGasPolicy>(
                new EthereumGasPolicy
                {
                    Value = regularGas,
                    StateReservoir = totalStateCost,
                    StateGasUsed = totalStateCost,
                    CostPerStateByte = costPerStateByte,
                },
                FromLong(floorCost, costPerStateByte))
            : new IntrinsicGas<EthereumGasPolicy>(FromLong(regularGas, costPerStateByte), FromLong(floorCost, costPerStateByte));
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
            CostPerStateByte = GetCostPerStateByte(in intrinsicGas),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CreateCost(Transaction tx, IReleaseSpec spec) =>
        tx.IsContractCreation && spec.IsEip2Enabled
            ? (spec.IsEip8037Enabled ? GasCostOf.CreateRegular : GasCostOf.TxCreate)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CreateStateCost(Transaction tx, IReleaseSpec spec, long costPerStateByte) =>
        tx.IsContractCreation && spec.IsEip8037Enabled ? GasCostOf.CalculateCreateState(costPerStateByte) : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DataCost(Transaction tx, IReleaseSpec spec, long tokensInCallData) =>
        spec.GetBaseDataCost(tx) + tokensInCallData * GasCostOf.TxDataZero;

}
