// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
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
    public ulong Value;
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
    /// <summary>Indicates that execution encountered an out of gas condition.</summary>
    public bool OutOfGas;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy FromULong(ulong value) => new() { Value = value };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) =>
        new()
        {
            Value = 0,
            StateReservoir = (long)Eip8037Constants.SystemCallStateReservoir,
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateSystemTransactionAvailableGas(ulong gasLimit, in EthereumGasPolicy intrinsicGas, IReleaseSpec spec)
    {
        if (spec.IsEip8037Enabled)
        {
            ulong reservoir = Math.Min(gasLimit, (ulong)intrinsicGas.StateReservoir);
            return new EthereumGasPolicy
            {
                Value = gasLimit - reservoir,
                StateReservoir = (long)reservoir,
                StateGasUsed = intrinsicGas.StateGasUsed,
                StateGasSpill = 0,
            };
        }

        return CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRemainingGas(in EthereumGasPolicy gas) => gas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStorageSetStateCost() => (long)GasCostOf.SSetState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCreateStateCost() => (long)GasCostOf.CreateState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetNewAccountStateCost() => (long)GasCostOf.NewAccountState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetPerAuthBaseStateCost() => (long)GasCostOf.PerAuthBaseState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetCodeDepositStateCost(int byteCodeLength) => (long)GasCostOf.CodeDepositState * byteCodeLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStorageSetReversalRefund() => (long)RefundOf.SSetReversedEip8037;

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
    public static void Consume(ref EthereumGasPolicy gas, ulong cost)
    {
        if (gas.Value < cost)
        {
            gas.Value = 0;
            gas.OutOfGas = true;
        }
        else
        {
            gas.Value -= cost;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsume(ref EthereumGasPolicy gas, ulong cost)
    {
        ulong v = gas.Value;
        if (v < cost) return false;
        gas.Value = v - cost;
        return true;
    }

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
        if (!TryConsume(ref gas, (ulong)spillAmount))
        {
            return false;
        }

        gas.StateReservoir = 0;
        gas.StateGasUsed += stateGasCost;
        gas.StateGasSpill += spillAmount;
        return true;
    }

    public static bool TryConsumeStateAndRegularGas(ref EthereumGasPolicy gas, long stateGasCost, ulong regularGasCost) =>
        (regularGasCost <= 0 || UpdateGas(ref gas, regularGasCost)) &&
        (stateGasCost <= 0 || ConsumeStateGas(ref gas, stateGasCost));

    public static bool ConsumeSelfDestructGas(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.SelfDestructEip150);

    /// <summary>
    /// Consume gas for code deposit. For standard Ethereum, this is equivalent to Consume.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeCodeDeposit(ref EthereumGasPolicy gas, ulong cost)
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
        // EELS refill_frame_state_gas (run by the reverting child) refills its net spill into
        // gas_left and returns only (used - spill) to the reservoir; the parent then absorbs the
        // child's gas_left. Crediting the spill back to the reservoir instead would leave it inflated,
        // so a subsequent top-level halt would return spilled gas the spec burns.
        long childNetSpill = Math.Max(0, childGas.StateGasSpill - childGas.StateGasSpillRefunded);
        parentGas.Value += (ulong)childNetSpill;
        parentGas.StateReservoir += childGas.StateReservoir + childGas.StateGasUsed - childNetSpill;
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
        // EELS refill_frame_state_gas on halt restores the reservoir by (used - spilled) and refills the
        // spilled portion into gas_left, which the halt then BURNS as regular gas. So only the
        // reservoir-funded portion returns to the parent; the child's net (unrefunded) spill stays burned.
        long childNetSpill = Math.Max(0, childGas.StateGasSpill - childGas.StateGasSpillRefunded);
        parentGas.StateReservoir += childGas.StateReservoir + childGas.StateGasUsed - childNetSpill;
        parentGas.StateGasSpill += childGas.StateGasSpill;
        parentGas.StateGasSpillRefunded += childGas.StateGasSpillRefunded;
        parentGas.StateGasSpillBurned += childGas.StateGasSpillBurned + childNetSpill;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RevertRefundToHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas)
    {
        // Code deposit failure is an exceptional halt of the child create frame, after the child was
        // merged into the parent. EELS refills the child's spilled state gas into gas_left and then
        // zeros gas_left (the halt burns it), so the spilled portion stays consumed. Only the
        // reservoir-funded state gas returns to the parent reservoir; crediting the spilled portion
        // too would over-refund — e.g. a failed CREATE deposit whose init wrote storage slots would
        // get its (spilled) storage state gas back instead of burning it.
        long childNetSpill = Math.Max(0, childGas.StateGasSpill - childGas.StateGasSpillRefunded);
        parentGas.StateReservoir += childGas.StateGasUsed - childNetSpill;
        parentGas.StateGasUsed -= childGas.StateGasUsed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetUnrefundedStateGasSpill(in EthereumGasPolicy childGas) =>
        Math.Max(0, childGas.StateGasSpill - childGas.StateGasSpillRefunded);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOutOfGas(in EthereumGasPolicy gas) => gas.OutOfGas;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref EthereumGasPolicy gas)
    {
        gas.Value = 0;
        gas.OutOfGas = true;
    }

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
        AccountAccessKind kind = AccountAccessKind.Default,
        bool hasCode = true)
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
            true => UpdateGas(ref gas, ColdAccountAccessCost(spec, hasCode)),
            false when kind == AccountAccessKind.SelfDestructBeneficiary => true,
            false => UpdateGas(ref gas, GasCostOf.WarmStateRead)
        };
    }

    // EIP-8038 reprices the (flat) cold account-access cost. devnet-6 dropped the earlier
    // EIP-2780 two-tier no-code discount, so the touch is independent of whether the account has code.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ColdAccountAccessCost(IReleaseSpec spec, bool hasCode) =>
        spec.IsEip8038Enabled ? Eip8038Constants.ColdAccountAccess
        : spec.IsEip2780Enabled && !hasCode ? GasCostOf.ColdAccountAccessNoCodeEip2780
        : GasCostOf.ColdAccountAccess;

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
            return UpdateGas(ref gas, spec.IsEip8038Enabled ? Eip8038Constants.ColdStorageAccess : GasCostOf.ColdSLoad);
        // EIP-8038 charges the warm-access cost on SSTORE too (the net-metered charge is dropped);
        // pre-8038, a warm SSTORE access is free here and the warm cost comes from net metering.
        if (storageAccessType == StorageAccessType.SLOAD || spec.IsEip8038Enabled)
            return UpdateGas(ref gas, GasCostOf.WarmStateRead);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref EthereumGasPolicy gas,
        in UInt256 position,
        in UInt256 length, ref EvmPooledMemory memory)
    {
        ulong memoryCost = memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost == 0L)
            return !outOfGas;
        return UpdateGas(ref gas, memoryCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref EthereumGasPolicy gas,
        in UInt256 position,
        ulong length, ref EvmPooledMemory memory)
    {
        ulong memoryCost = memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost == 0)
            return !outOfGas;
        return UpdateGas(ref gas, memoryCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(ref EthereumGasPolicy gas,
        ulong gasCost)
    {
        if (GetRemainingGas(in gas) < gasCost)
        {
            gas.Value = 0;
            gas.OutOfGas = true;
            return false;
        }

        Consume(ref gas, gasCost);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref EthereumGasPolicy gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
    {
        // EIP-8038 reprices the SSTORE write component (charged on the first change to a slot,
        // for both fresh slots and resets) to a flat STORAGE_WRITE.
        if (!TIsSlotCreation.IsActive)
            return UpdateGas(ref gas, spec.IsEip8038Enabled ? Eip8038Constants.StorageWrite : spec.GasCosts.SStoreResetCost);

        ulong regularWriteCost = spec.IsEip8038Enabled ? Eip8038Constants.StorageWrite : GasCostOf.SSetRegular;
        return TEip8037.IsActive switch
        {
            // EIP-8037: charge the regular component first so an OOG halt does not
            // spill state gas into gas_left and then restore it to the parent frame.
            true => TryConsumeStateAndRegularGas(ref gas, GetStorageSetStateCost(), regularWriteCost),
            false => UpdateGas(ref gas, GasCostOf.SSet),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref EthereumGasPolicy gas,
        ulong refund) => gas.Value += refund;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor)
        => RefundStateGas(ref gas, amount, stateGasFloor, trackSpillRefund: true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor, bool trackSpillRefund)
    {
        // All callers guard amount >= 0; a negative amount would wrap gas.Value via (ulong)toGasLeft below.
        Debug.Assert(amount >= 0, $"Negative state-gas refund ({amount}).");
        long refundableStateGas = Math.Max(0, gas.StateGasUsed - stateGasFloor);
        long appliedRefund = Math.Min(amount, refundableStateGas);
        long toGasLeft = 0;
        if (trackSpillRefund)
        {
            // Source-based LIFO refill (EELS credit_state_gas_refund): a state-gas refund returns to the
            // pools the charge drew from — gas_left first, up to the amount that spilled, then the
            // reservoir. Crediting spilled gas back to gas_left (not the reservoir) keeps the runtime
            // reservoir at the value the spec expects, so e.g. a reverted sub-frame that spilled its
            // SSTORE state gas does not leave the caller's reservoir inflated.
            toGasLeft = Math.Min(appliedRefund, GetUnrefundedStateGasSpill(in gas));
            gas.StateGasSpillRefunded += toGasLeft;
        }

        gas.Value += (ulong)toGasLeft;
        gas.StateReservoir += appliedRefund - toGasLeft;
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
    public static ulong GetCodeInsertRegularRefund(ulong codeInsertRefunds, IReleaseSpec spec)
    {
        if (codeInsertRefunds == 0UL) return 0;
        // EIP-8038: per existing-authority EIP-7702 refund, the worst-case ACCOUNT_WRITE charged in the
        // intrinsic is returned to the regular-gas refund counter (the NEW_ACCOUNT/AUTH_BASE state refunds
        // are applied separately in Apply8037DelegationRefunds).
        if (spec.IsEip8038Enabled) return Eip8038Constants.AccountWrite * codeInsertRefunds;
        if (spec.IsEip8037Enabled) return 0;
        return (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ApplyCodeInsertRefunds(ref EthereumGasPolicy gas, ulong codeInsertRefunds, IReleaseSpec spec, long stateGasFloor)
        // Under EIP-8037 the per-authorization state refund is applied pre-execution in
        // Apply8037DelegationRefunds; only the regular refund is surfaced for the refund counter here.
        => GetCodeInsertRegularRefund(codeInsertRefunds, spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeCallValueTransfer(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.CallValue);

    // EIP-2780 value-moving call cost. Under EIP-8038 a value-bearing call charges a flat CALL_VALUE
    // (the new-account surcharge moves to a separate NEW_ACCOUNT state charge); the earlier draft used
    // a three-tier charge keyed on self-call and recipient existence that subsumed the surcharge.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeCallValueTransferEip2780(ref EthereumGasPolicy gas, bool isSelfCall, bool recipientEmpty, IReleaseSpec spec)
    {
        if (spec.IsEip8038Enabled)
            return UpdateGas(ref gas, Eip8038Constants.CallValue);

        ulong cost = isSelfCall ? GasCostOf.CallValueSelfEip2780
            : recipientEmpty ? GasCostOf.CallValueNewAccountEip2780
            : GasCostOf.CallValueExistingEip2780;
        return UpdateGas(ref gas, cost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeNewAccountCreation<TEip8037>(ref EthereumGasPolicy gas) where TEip8037 : struct, IFlag => TEip8037.IsActive switch
    {
        true => ConsumeStateGas(ref gas, GetNewAccountStateCost()),
        false => UpdateGas(ref gas, GasCostOf.NewAccount)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeLogEmission(ref EthereumGasPolicy gas, ulong topicCount, ulong dataSize)
    {
        ulong cost = GasCostOf.Log + topicCount * GasCostOf.LogTopic + dataSize * GasCostOf.LogData;
        return UpdateGas(ref gas, cost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeDataCopyGas(ref EthereumGasPolicy gas, bool isExternalCode, ulong baseCost, ulong dataCost)
        => Consume(ref gas, baseCost + dataCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnBeforeInstructionTrace(in EthereumGasPolicy gas, int pc, Instruction instruction, int depth) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnAfterInstructionTrace(in EthereumGasPolicy gas) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeDataCopyGas(ref EthereumGasPolicy gas, IReleaseSpec spec, bool isExternalCode, ulong words)
        => Consume(ref gas, (isExternalCode ? spec.GasCosts.ExtCodeCost : GasCostOf.VeryLow) + GasCostOf.Memory * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CombineBlockGas(ulong blockRegularGas, ulong blockStateGas) =>
        Math.Max(blockRegularGas, blockStateGas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy Max(in EthereumGasPolicy a, in EthereumGasPolicy b) =>
        a.Value >= b.Value ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateChildFrameGas(ref EthereumGasPolicy parentGas, ulong childRegularGas)
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

    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit, IReadOnlyStateProvider? worldState = null)
    {
        ulong tokensInCallData = IGasPolicy<EthereumGasPolicy>.CalculateTokensInCallData(tx, spec);
        ulong floorTokensInAccessList = IGasPolicy<EthereumGasPolicy>.CalculateFloorTokensInAccessList(tx, spec);
        (ulong authRegularCost, ulong authStateCost) = IGasPolicy<EthereumGasPolicy>.AuthorizationListCost(tx, spec);
        ulong accessListCost = IGasPolicy<EthereumGasPolicy>.AccessListCost(tx, spec, floorTokensInAccessList);

        ulong baseCost = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        ulong regularGas = baseCost
                          + DataCost(tx, spec, tokensInCallData)
                          + CreateCost(tx, spec)
                          + accessListCost
                          + authRegularCost
                          + Eip2780ExtraGas(tx, spec, worldState);
        ulong floorCost = IGasPolicy<EthereumGasPolicy>.CalculateFloorCost(tx, spec, tokensInCallData, floorTokensInAccessList);
        ulong createStateCost = CreateStateCost(tx, spec);
        ulong totalStateCost = authStateCost + createStateCost;
        return spec.IsEip8037Enabled
            ? new IntrinsicGas<EthereumGasPolicy>(
                new EthereumGasPolicy
                {
                    Value = regularGas,
                    StateReservoir = (long)totalStateCost,
                    StateGasUsed = (long)totalStateCost,
                },
                FromULong(floorCost))
            : new IntrinsicGas<EthereumGasPolicy>(FromULong(regularGas), FromULong(floorCost));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateAvailableFromIntrinsic(ulong gasLimit, in EthereumGasPolicy intrinsicGas, IReleaseSpec spec)
    {
        // Callers must validate intrinsic gas against gasLimit (ValidateIntrinsicGas / Eip8037.ExceedsCap)
        // before calling; if they don't, the subtraction wraps silently.
        Debug.Assert(gasLimit >= intrinsicGas.Value + (ulong)intrinsicGas.StateReservoir,
            $"gasLimit ({gasLimit}) < intrinsicRegular ({intrinsicGas.Value}) + intrinsicState ({intrinsicGas.StateReservoir})");
        Debug.Assert(!spec.IsEip8037Enabled || Eip7825Constants.DefaultTxGasLimitCap >= intrinsicGas.Value,
            "Eip8037 enabled but intrinsicRegular exceeds tx gas cap.");

        ulong executionGas = gasLimit - intrinsicGas.Value - (ulong)intrinsicGas.StateReservoir;
        ulong reservoir = 0;

        if (spec.IsEip8037Enabled)
        {
            // EIP-8037: cap gas_left at TX_MAX_GAS_LIMIT - intrinsic_regular, overflow goes to reservoir
            ulong maxGasLeft = Eip7825Constants.DefaultTxGasLimitCap - intrinsicGas.Value;
            reservoir = executionGas.SaturatingSub(maxGasLeft);
            executionGas -= reservoir;
        }

        return new EthereumGasPolicy
        {
            Value = executionGas,
            StateReservoir = (long)reservoir,
            StateGasUsed = intrinsicGas.StateReservoir,
            StateGasSpill = 0,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CreateCost(Transaction tx, IReleaseSpec spec) =>
        tx.IsContractCreation && spec.IsEip2Enabled
            ? (spec.IsEip8038Enabled ? Eip8038Constants.CreateAccess
                : spec.IsEip8037Enabled ? GasCostOf.CreateRegular
                : GasCostOf.TxCreate)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CreateStateCost(Transaction tx, IReleaseSpec spec) =>
        tx.IsContractCreation && spec.IsEip8037Enabled ? GasCostOf.CreateState : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DataCost(Transaction tx, IReleaseSpec spec, ulong tokensInCallData) =>
        spec.GetBaseDataCost(tx) + tokensInCallData * GasCostOf.TxDataZero;

    /// <summary>
    /// EIP-2780 recipient charge on top of TX_BASE_COST. Dispatches to the EIP-8038 (glamsterdam-devnet-6)
    /// flat model when EIP-8038 is active, otherwise to the standalone EIP-2780 two-tier model.
    /// </summary>
    /// <remarks>
    /// Both models mirror their respective EELS <c>calculate_intrinsic_cost</c>: the recipient touch overrides
    /// EIP-2929's "all tx addresses are warm" rule and the recipient stays pre-warmed for execution. See
    /// <see cref="Eip8038IntrinsicRecipientGas(Transaction)"/> and <see cref="Eip2780StandaloneExtraGas"/> for the specifics.
    /// </remarks>
    private static ulong Eip2780ExtraGas(Transaction tx, IReleaseSpec spec, IReadOnlyStateProvider? worldState)
    {
        if (!spec.IsEip2780Enabled) return 0;
        return spec.IsEip8038Enabled
            ? Eip8038IntrinsicRecipientGas(tx)
            : Eip2780StandaloneExtraGas(tx, spec, worldState);
    }

    // EIP-8038 (glamsterdam-devnet-6): the recipient touch is a flat cold charge independent of the
    // recipient's existence or code, and a value transfer adds the EIP-7708 transfer log plus a fixed
    // value-move cost. The new-account surcharge moves to a separate NEW_ACCOUNT state charge, and the
    // EIP-7702 delegation-target touch is charged at execution time (against post-authorization state)
    // rather than here; neither is priced in this method. State-independent because the EELS
    // intrinsic cost does not consult state.
    private static ulong Eip8038IntrinsicRecipientGas(Transaction tx)
    {
        bool hasValue = !tx.Value.IsZero;

        if (tx.IsContractCreation)
            return hasValue ? GasCostOf.TransferLogEip2780 : 0;

        // Self-transfers coalesce into the sender leaf write already priced into TX_BASE_COST.
        if (tx.SenderAddress == tx.To) return 0;

        ulong cost = Eip8038Constants.ColdAccountAccess;
        if (hasValue)
            cost += GasCostOf.TransferLogEip2780 + GasCostOf.TxValueCostEip2780;

        return cost;
    }

    // Standalone EIP-2780 (pre-EIP-8038) intrinsic recipient cost: a two-tier cold touch keyed on
    // whether the recipient carries code, plus a NEW_ACCOUNT surcharge and a STATE_UPDATE leaf write
    // for value transfers, mirroring the EIP-2780 reference table. Requires <paramref name="worldState"/>
    // to classify the recipient; superseded by <see cref="Eip8038IntrinsicRecipientGas(Transaction)"/> under EIP-8038.
    private static ulong Eip2780StandaloneExtraGas(Transaction tx, IReleaseSpec spec, IReadOnlyStateProvider? worldState)
    {
        bool isCreate = tx.IsContractCreation;
        Address? to = tx.To;
        bool hasValue = !tx.Value.IsZero;
        bool senderIsRecipient = !isCreate && tx.SenderAddress == to;

        ulong cost = 0;
        // EIP-7708 transfer log on the top-level value transfer; CREATE endows a distinct address.
        if (hasValue && (isCreate || !senderIsRecipient))
            cost += GasCostOf.TransferLogEip2780;

        if (isCreate || to is null || worldState is null) return cost;

        bool isPrecompile = spec.IsPrecompile(to);
        bool recipientDead = worldState.IsDeadAccount(to);

        // New-account surcharge: value transfer to a nonexistent, non-precompile recipient.
        if (hasValue && !isPrecompile && recipientDead)
            cost += GasCostOf.NewAccount;

        // Self-transfers coalesce into the sender leaf write already priced into TX_BASE_COST;
        // precompiles are warm at tx start and charged zero.
        if (!senderIsRecipient && !isPrecompile)
        {
            cost += RecipientTouchCost(tx, spec, worldState, to);
            // The new-account surcharge already covers the recipient leaf write.
            if (hasValue && !recipientDead)
                cost += GasCostOf.StateUpdateEip2780;
        }

        return cost;
    }

    private static ulong RecipientTouchCost(Transaction tx, IReleaseSpec spec, IReadOnlyStateProvider worldState, Address to)
    {
        bool toHasCode = worldState.IsContract(to);
        ulong cost = IsInAccessList(tx, to)
            ? GasCostOf.WarmStateRead
            : ColdAccountAccessCost(spec, toHasCode);

        // EIP-7702: a delegated recipient also touches its delegation target (always carries code).
        // The EVM warms (does not gas-charge) this target for the top-level frame, so this is the sole charge.
        // Only an account with code can carry a delegation, so plain EOAs skip the code read entirely.
        if (spec.IsEip7702Enabled && toHasCode
            && ICodeInfoRepository.TryGetDelegatedAddress(worldState.GetCode(to).AsSpan(), out Address? target))
            cost += IsInAccessList(tx, target) ? GasCostOf.WarmStateRead : ColdAccountAccessCost(spec, hasCode: true);

        return cost;
    }

    // A single linear scan beats materialising a HashSet: access lists are small and we probe at
    // most two addresses (the recipient and an optional EIP-7702 delegation target) per transaction.
    private static bool IsInAccessList(Transaction tx, Address? address)
    {
        if (tx.AccessList is null) return false;
        foreach ((Address entry, _) in tx.AccessList)
        {
            if (entry == address) return true;
        }
        return false;
    }

}
