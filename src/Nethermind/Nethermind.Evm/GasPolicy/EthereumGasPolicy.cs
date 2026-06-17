// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
/// <remarks>
/// The spill split fields below follow EIP-8037 block-gas accounting.
/// </remarks>
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
    /// <summary>Tx-cumulative spill from reverted child frames used by top-level halt accounting.</summary>
    public long StateGasSpillBurned;
    /// <summary>Spill that should remain in the block regular dimension.</summary>
    public long StateGasSpillReclassified;
    /// <summary>Spill consumed by state refunds and excluded from block regular gas.</summary>
    public long StateGasSpillRefunded;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy FromLong(long value) => new() { Value = value };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateSystemTransactionIntrinsicGas(long blockGasLimit) =>
        new()
        {
            Value = 0,
            StateReservoir = Eip8037Constants.SystemCallStateReservoir,
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateSystemTransactionAvailableGas(long gasLimit, in EthereumGasPolicy intrinsicGas, IReleaseSpec spec)
    {
        if (spec.IsEip8037Enabled)
        {
            return new EthereumGasPolicy
            {
                Value = gasLimit - intrinsicGas.StateReservoir,
                StateReservoir = intrinsicGas.StateReservoir,
                StateGasUsed = intrinsicGas.StateGasUsed,
                StateGasSpill = 0,
            };
        }

        return CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetRemainingGas(in EthereumGasPolicy gas) => gas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStorageSetStateCost(in EthereumGasPolicy gas) => GasCostOf.SSetState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCreateStateCost(in EthereumGasPolicy gas) => GasCostOf.CreateState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetNewAccountStateCost(in EthereumGasPolicy gas) => GasCostOf.NewAccountState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetPerAuthBaseStateCost(in EthereumGasPolicy gas) => GasCostOf.PerAuthBaseState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCodeDepositStateCost(in EthereumGasPolicy gas, int byteCodeLength) => GasCostOf.CodeDepositState * byteCodeLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStorageSetReversalRefund(in EthereumGasPolicy gas) => RefundOf.SSetReversedEip8037;

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
    }

    // On explicit REVERT, restore the child's remaining state reservoir plus its reverted
    // state gas usage. Propagate spill so block-regular accounting can exclude it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGas(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas)
    {
        parentGas.StateReservoir += childGas.StateReservoir + childGas.StateGasUsed;
        parentGas.StateGasSpill += childGas.StateGasSpill;
        parentGas.StateGasSpillBurned += childGas.StateGasSpillBurned;
        parentGas.StateGasSpillReclassified += childGas.StateGasSpillReclassified;
        parentGas.StateGasSpillRefunded += childGas.StateGasSpillRefunded;
    }

    // On child exceptional halt, regular gas in the child frame is consumed, but state gas did
    // not produce durable state growth. Restore the child's state reservoir and state usage to
    // the parent reservoir without adding the child's state usage to parent block-state usage.
    // Any child state spill remains state-attributed for block regular gas accounting.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGasOnHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas)
    {
        parentGas.StateReservoir += childGas.StateReservoir + childGas.StateGasUsed;
        parentGas.StateGasSpill += childGas.StateGasSpill;
        parentGas.StateGasSpillBurned += childGas.StateGasSpillBurned;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RevertRefundToHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas)
    {
        // Code deposit failure is an exceptional halt after the child frame was merged into
        // the parent. Move the child's state usage back into the reservoir and remove it from
        // parent state usage because no child state growth persisted.
        parentGas.StateReservoir += childGas.StateGasUsed;
        parentGas.StateGasUsed -= childGas.StateGasUsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetUnrefundedStateGasSpill(in EthereumGasPolicy childGas) =>
        Math.Max(0, childGas.StateGasSpill - childGas.StateGasSpillRefunded);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref EthereumGasPolicy gas) => gas.Value = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeAccountAccessGasWithDelegation(ref EthereumGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        Address? delegated)
    {
        if (!spec.UseHotAndColdStorage)
            return true;

        bool notOutOfGas = ConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, address);
        return notOutOfGas && (delegated is null || ConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, delegated));
    }

    public static bool ConsumeAccountAccessGas(ref EthereumGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        AccountAccessKind kind = AccountAccessKind.Default)
    {
        if (!spec.UseHotAndColdStorage) return true;
        if (isTracingAccess)
        {
            // Ensure that tracing simulates access-list behavior.
            accessTracker.WarmUp(address);
        }

        // WarmUp first so the warm path skips IsPrecompile; charged gas matches (!IsPrecompile && WarmUp).
        // Precompiles are pre-warmed at tx start, so WarmUp(precompile) is already-warm and the reorder is moot.
        return (accessTracker.WarmUp(address) && !spec.IsPrecompile(address)) switch
        {
            true => UpdateGas(ref gas, GasCostOf.ColdAccountAccess),
            false when kind == AccountAccessKind.SelfDestructBeneficiary => true,
            false => UpdateGas(ref gas, GasCostOf.WarmStateRead)
        };
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
        if (trackSpillRefund)
        {
            TrackStateGasSpillRefund(ref gas, appliedRefund);
        }

        gas.StateReservoir += appliedRefund;
        gas.StateGasUsed -= appliedRefund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DiscardStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor, bool trackSpillRefund)
    {
        long discardableStateGas = Math.Max(0, gas.StateGasUsed - stateGasFloor);
        long appliedRefund = Math.Min(amount, discardableStateGas);
        if (trackSpillRefund)
        {
            TrackStateGasSpillRefund(ref gas, appliedRefund);
        }

        gas.StateGasUsed -= appliedRefund;
        return amount - appliedRefund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddStateGasRefundToReservoir(ref EthereumGasPolicy gas, long amount, bool trackSpillRefund)
    {
        if (trackSpillRefund)
        {
            TrackStateGasSpillRefund(ref gas, amount);
        }

        gas.StateReservoir += amount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RemoveStateGasRefundFromReservoir(ref EthereumGasPolicy gas, long amount)
    {
        long fromReservoir = Math.Min(amount, gas.StateReservoir);
        gas.StateReservoir -= fromReservoir;
        amount -= fromReservoir;

        if (amount > 0)
        {
            gas.StateGasUsed -= Math.Min(amount, gas.StateGasUsed);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TrackStateGasSpillRefund(ref EthereumGasPolicy gas, long amount)
    {
        long unrefundedSpill = GetUnrefundedStateGasSpill(in gas);
        gas.StateGasSpillRefunded += Math.Min(amount, unrefundedSpill);
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
            long stateGasRefund = checked(GetNewAccountStateCost(in gas) * codeInsertRefunds);
            long refundFloor = Math.Max(0, stateGasFloor - stateGasRefund);
            RefundStateGas(ref gas, stateGasRefund, refundFloor, trackSpillRefund: false);
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
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);

    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, long blockGasLimit)
    {
        long tokensInCallData = IGasPolicy<EthereumGasPolicy>.CalculateTokensInCallData(tx, spec);
        long floorTokensInAccessList = IGasPolicy<EthereumGasPolicy>.CalculateFloorTokensInAccessList(tx, spec);
        (long authRegularCost, long authStateCost) = IGasPolicy<EthereumGasPolicy>.AuthorizationListCost(tx, spec);
        long accessListCost = IGasPolicy<EthereumGasPolicy>.AccessListCost(tx, spec, floorTokensInAccessList);

        long regularGas = GasCostOf.Transaction
                          + DataCost(tx, spec, tokensInCallData)
                          + CreateCost(tx, spec)
                          + accessListCost
                          + authRegularCost;
        long floorCost = IGasPolicy<EthereumGasPolicy>.CalculateFloorCost(tx, spec, tokensInCallData, floorTokensInAccessList);
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
