// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    static abstract TSelf FromULong(ulong value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual TSelf CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) => TSelf.FromULong(0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryCreateSystemTransactionAvailableGas(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec, out TSelf available) =>
        TSelf.TryCreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec, out available);

    static abstract ulong GetRemainingGas(in TSelf gas);

    /// <summary>Zeros execution gas without changing state-gas accounting.</summary>
    static abstract void ClearExecutionGas(ref TSelf gas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong CombineBlockGas(ulong blockExecutionGas, ulong blockStateGas) => Math.Max(blockExecutionGas, blockStateGas);

    /// <summary>EIP-8037 pre-refund spent gas: <c>txGasLimit - gas_left - state reservoir</c>.</summary>
    /// <remarks>
    /// Centralizes the execution↔state boundary conversion: the reservoir may be negative due to net child spill.
    /// If the accounting invariant is violated, the full transaction gas limit is returned to keep accounting conservative
    /// without unsigned wraparound.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong GetPreRefundGas(in TSelf gas, ulong txGasLimit)
    {
        ulong remainingGas = TSelf.GetRemainingGas(in gas);
        long stateReservoir = TSelf.GetStateReservoir(in gas);
        Int128 preRefundGas = (Int128)txGasLimit - remainingGas - stateReservoir;
        bool inRange = preRefundGas >= 0 && preRefundGas <= ulong.MaxValue;
        Debug.Assert(inRange,
            $"Gas invariant violated: pre-refund gas ({preRefundGas}) must fit in ulong for gas limit ({txGasLimit}), remaining gas ({remainingGas}), and state reservoir ({stateReservoir}).");
        // Charging the full limit avoids undercharging and makes validation reject divergent gas accounting.
        return inRange ? (ulong)preRefundGas : txGasLimit;
    }

    // EIP-8037 state-cost accessors. Pre-EIP-8037 policies return the constant fallback.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStorageSetStateCost() => GasCostOf.SSetState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetCreateStateCost() => GasCostOf.CreateState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetNewAccountStateCost() => GasCostOf.NewAccountState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetPerAuthBaseStateCost() => GasCostOf.PerAuthBaseState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetCodeDepositStateCost(int byteCodeLength) => GasCostOf.CodeDepositState * byteCodeLength;

    // EIP-8037 state-accounting accessors. Pre-EIP-8037 policies return 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateReservoir(in TSelf gas) => 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasUsed(in TSelf gas) => 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasSpill(in TSelf gas) => 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong CalculateStateGasSpill(in TSelf gas, long stateGasCost)
    {
        if (stateGasCost <= 0)
        {
            return 0;
        }

        long reservoirContribution = TSelf.GetStateReservoir(in gas);
        if (reservoirContribution <= 0)
        {
            return (ulong)stateGasCost;
        }

        return stateGasCost > reservoirContribution
            ? (ulong)(stateGasCost - reservoirContribution)
            : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsume(ref TSelf gas, ulong cost)
    {
        if (TSelf.GetRemainingGas(in gas) < cost) return false;
        return TSelf.UpdateGas(ref gas, cost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool UpdateGas<TCost>(ref TSelf gas) where TCost : struct, IGasCost =>
        TSelf.UpdateGas(ref gas, TCost.GasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool UpdateGas<TCost>(ref TSelf gas, IReleaseSpec spec) where TCost : struct, ISpecGasCost =>
        TSelf.UpdateGas(ref gas, TCost.GasCost(spec));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeKeccak(ref TSelf gas, ulong words) =>
        TSelf.UpdateGas(ref gas, GasCostOf.Sha3 + GasCostOf.Sha3Word * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeMemoryCopy(ref TSelf gas, ulong words) =>
        TSelf.UpdateGas(ref gas, GasCostOf.VeryLow + GasCostOf.VeryLow * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeExpBytes(ref TSelf gas, IReleaseSpec spec, ulong exponentByteSize) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.ExpByteCost * exponentByteSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeCreateGas<TEip8037, TOpCreate>(ref TSelf gas, IReleaseSpec spec, ulong initCodeWords)
        where TEip8037 : struct, IFlag
        where TOpCreate : struct, EvmInstructions.IOpCreate
    {
        ulong baseCost = spec.IsEip8038Enabled ? Eip8038Constants.CreateAccess
            : TEip8037.IsActive ? GasCostOf.CreateExecution
            : GasCostOf.Create;
        ulong initCodeWordCost = spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * initCodeWords : 0;
        ulong create2HashCost = typeof(TOpCreate) == typeof(EvmInstructions.OpCreate2) ? GasCostOf.Sha3Word * initCodeWords : 0;
        return TSelf.UpdateGas(ref gas, baseCost + initCodeWordCost + create2HashCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeCallBaseGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.CallCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeSStoreResetGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.SStoreResetCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeNetMeteredSStoreGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.NetMeteredSStoreCost);

    /// <summary>Charges net-metered storage using the selected EIP-8038 mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeNetMeteredSStoreGas<Eip8038>(ref TSelf gas, IReleaseSpec spec)
        where Eip8038 : struct, IFlag => TSelf.TryConsumeNetMeteredSStoreGas(ref gas, spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeSSetFromCleanGas(ref TSelf gas) =>
        TSelf.UpdateGas(ref gas, GasCostOf.SSet - GasCostOf.SReset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumePrecompileGas(ref TSelf gas, IPrecompile precompile, ReadOnlyMemory<byte> inputData, IReleaseSpec spec)
    {
        ulong baseGasCost = precompile.BaseGasCost(spec);
        ulong dataGasCost = precompile.DataGasCost(inputData, spec);
        return baseGasCost <= ulong.MaxValue - dataGasCost && TSelf.UpdateGas(ref gas, baseGasCost + dataGasCost);
    }
    static abstract bool TryConsumeSelfDestructGas(ref TSelf gas);
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

    /// <summary>Repays outstanding EIP-8037 state-gas spill from the reservoir after a successful child merge.</summary>
    /// <remarks>
    /// Implements the EIP-8037 <c>d = min(state_gas_reservoir, state_gas_from_gas_left)</c> merge step.
    /// Policies that retain total spill separately record <c>d</c> as repaid spill instead of reducing that total.
    /// Policies that implement this step leave either the reservoir or the outstanding spill exhausted.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RepayStateGasSpill(ref TSelf gas) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeCreateStateGas(ref TSelf gas) =>
        TSelf.TryConsumeStateGas(ref gas, TSelf.GetCreateStateCost());

    // Revert path: restore the child's state gas into the parent reservoir.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RestoreChildStateGas(ref TSelf parentGas, in TSelf childGas) { }
    // Halt path: preserve inline state-gas refunds (call chain resets to top-most failing call).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RestoreChildStateGasOnHalt(ref TSelf parentGas, in TSelf childGas) { }
    // Code-deposit-failure path: undo prior Refund's state-gas merge and apply halt restoration.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RevertRefundToHalt(ref TSelf parentGas, in TSelf childGas) { }

    static abstract bool TryConsumeAccountAccessGasWithDelegation(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        Address? delegated);

    static abstract bool TryConsumeAccountAccessGas(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        AccountAccessKind kind = AccountAccessKind.Default);

    static abstract bool TryConsumeStorageAccessGas(ref TSelf gas,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec);

    /// <summary>Charges storage access using the selected fork flags.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeStorageAccessGas<Eip2929, Eip8038>(ref TSelf gas,
        ref readonly StackAccessTracker accessTracker, bool isTracingAccess,
        in StorageCell storageCell, StorageAccessType storageAccessType, IReleaseSpec spec)
        where Eip2929 : struct, IFlag
        where Eip8038 : struct, IFlag =>
        TSelf.TryConsumeStorageAccessGas(ref gas, in accessTracker, isTracingAccess, in storageCell, storageAccessType, spec);

    /// <summary>Charges account access using the selected fork flags.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeAccountAccessGas<Eip2929, Eip8038>(ref TSelf gas, IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker, bool isTracingAccess, Address address,
        AccountAccessKind kind = AccountAccessKind.Default)
        where Eip2929 : struct, IFlag
        where Eip8038 : struct, IFlag =>
        TSelf.TryConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, address, kind);

    /// <summary>Reserves CALL gas using the selected EIP-150 mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryReserveChildGas<Eip150>(ref TSelf gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas)
        where Eip150 : struct, IFlag => TSelf.TryReserveChildGas(ref gas, in requestedGas, spec, out childGas);

    /// <summary>Reserves CREATE gas using the selected EIP-150 mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryReserveChildGas<Eip150>(ref TSelf gas, IReleaseSpec spec, out ulong childGas)
        where Eip150 : struct, IFlag => TSelf.TryReserveChildGas(ref gas, spec, out childGas);

    /// <summary>Charges creation using the selected fork flags.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeCreateGas<Eip8037, TOpCreate, Eip3860, Eip8038>(ref TSelf gas, IReleaseSpec spec, ulong initCodeWords)
        where Eip8037 : struct, IFlag
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where Eip3860 : struct, IFlag
        where Eip8038 : struct, IFlag => TSelf.TryConsumeCreateGas<Eip8037, TOpCreate>(ref gas, spec, initCodeWords);

    static abstract bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, in UInt256 length, ref EvmPooledMemory memory);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, ulong length, ref EvmPooledMemory memory)
    {
        UInt256 uint256Length = new(length);
        return TSelf.UpdateMemoryCost(ref gas, in position, in uint256Length, ref memory);
    }

    /// <summary>Charges execution gas, returning false and exhausting it when the cost is unaffordable.</summary>
    static abstract bool UpdateGas(ref TSelf gas, ulong gasCost);

    // Pre-EIP-8037 fallback: state gas folded into execution gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeStateGas(ref TSelf gas, long stateGasCost) => TSelf.UpdateGas(ref gas, (ulong)stateGasCost);

    // Execution gas charged first to prevent state-gas spill-then-halt from inflating
    // the reservoir via the error refund path.
    static abstract bool TryConsumeStateAndExecutionGas(ref TSelf gas, long stateGasCost, ulong executionGasCost);

    static abstract void UpdateGasUp(ref TSelf gas, ulong refund);

    static abstract bool TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref TSelf gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag;

    /// <summary>Charges a storage write using the selected EIP-8038 mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsumeStorageWrite<Eip8037, TIsSlotCreation, Eip8038>(ref TSelf gas, IReleaseSpec spec)
        where Eip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
        where Eip8038 : struct, IFlag =>
        TSelf.TryConsumeStorageWrite<Eip8037, TIsSlotCreation>(ref gas, spec);

    // Pre-EIP-8037 fallback: refund into execution gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RefundStateGas(ref TSelf gas, long amount, long stateGasFloor) => TSelf.UpdateGasUp(ref gas, (ulong)amount);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RefundStateGas(ref TSelf gas, long amount, long stateGasFloor, bool trackSpillRefund) =>
        TSelf.RefundStateGas(ref gas, amount, stateGasFloor);

    // Drop state-gas from block-state accounting without refunding to the gas budget;
    // reverted state charges stay paid by the tx but don't contribute to committed state gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long DiscardStateGas(ref TSelf gas, long amount, long stateGasFloor) => amount;

    /// <summary>Credits a speculative state-gas refund to the frame, continuing the source-based
    /// LIFO refill: gas_left up to the frame's unrefunded spill, the remainder to the reservoir.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void AddStateGasRefundToReservoir(ref TSelf gas, long amount, bool trackSpillRefund)
        => TSelf.UpdateGasUp(ref gas, (ulong)amount);

    /// <summary>Revokes a speculative refund credited by <see cref="AddStateGasRefundToReservoir"/>.</summary>
    /// <remarks>Claws the full amount from the reservoir (negative if needed); the gas_left-refilled
    /// portion stays there, its permanent spill-refund mark keeping the net spill consistent.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RemoveStateGasRefundFromReservoir(ref TSelf gas, long amount) { }

    // EIP-8037 top-level halt: snap state-gas back to (R0, intrinsicStateUsed, 0); the
    // post-reset StateGasUsed feeds SpentGas so the user doesn't pay for uncommitted state.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ResetForHalt(ref TSelf gas, long initialStateReservoir, long initialStateGasUsed) { }

    /// <summary>Folds EIP-8037 top-frame state gas into the rollback baseline.</summary>
    /// <remarks>Used for preparation charges, such as EIP-7702 authorization writes, that survive execution rollback.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void FoldTopFrameStateGas(ref TSelf gas, ref TSelf baseline, long stateGasUsed) { }

    // EIP-7702 code-insert refund execution-gas portion. Pre-EIP-8037: (NewAccount - PerAuthBaseCost) each.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong GetCodeInsertExecutionRefund(ulong codeInsertRefunds, IReleaseSpec spec) =>
        codeInsertRefunds > 0UL ? (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds : 0UL;

    // EIP-8037: replenishes tx state reservoir before exec (intrinsic state gas already charged).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong ApplyCodeInsertRefunds(ref TSelf gas, ulong codeInsertRefunds, IReleaseSpec spec, long stateGasFloor) =>
        TSelf.GetCodeInsertExecutionRefund(codeInsertRefunds, spec);

    static abstract bool TryConsumeCallValueTransfer(ref TSelf gas);
    static abstract bool TryConsumeCallValueTransferEip2780(ref TSelf gas);
    static abstract bool TryConsumeNewAccountCreation<TEip8037>(ref TSelf gas) where TEip8037 : struct, IFlag;
    static abstract bool TryConsumeLogEmission(ref TSelf gas, ulong topicCount, ulong dataSize);
    static abstract TSelf Max(in TSelf a, in TSelf b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        TSelf.CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);
    static abstract IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit);

    static abstract bool TryCreateAvailableFromIntrinsic(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec, out TSelf available);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual TSelf CreateChildFrameGas(ref TSelf parentGas, ulong childExecutionGas) => TSelf.FromULong(childExecutionGas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryReserveChildGas(ref TSelf gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas)
    {
        ulong gasAvailable = TSelf.GetRemainingGas(in gas);
        if (spec.Use63Over64Rule)
        {
            ulong cap = gasAvailable - gasAvailable / 64;
            childGas = requestedGas.IsUint64 && requestedGas.u0 <= cap ? requestedGas.u0 : cap;
        }
        else
        {
            if (!requestedGas.IsUint64)
            {
                childGas = 0;
                return false;
            }
            childGas = requestedGas.u0;
        }
        return TSelf.UpdateGas(ref gas, childGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryReserveChildGas(ref TSelf gas, IReleaseSpec spec, out ulong childGas)
    {
        ulong gasAvailable = TSelf.GetRemainingGas(in gas);
        childGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64 : gasAvailable;
        return TSelf.UpdateGas(ref gas, childGas);
    }

    // EXTCODECOPY may need different categorization (state trie access) for some policies.
    static abstract bool TryConsumeDataCopyGas(ref TSelf gas, IReleaseSpec spec, bool isExternalCode, ulong words);
}

public readonly record struct IntrinsicGas<TGasPolicy>(TGasPolicy Standard, TGasPolicy FloorGas)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public TGasPolicy MinimalGas { get; } = TGasPolicy.Max(Standard, FloorGas);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator TGasPolicy(IntrinsicGas<TGasPolicy> gas) => gas.MinimalGas;

    // The intrinsic reservoir holds the intrinsic state cost, non-negative by construction, so the cast cannot wrap.
    public ulong StandardGas => TGasPolicy.GetRemainingGas(Standard) + (ulong)TGasPolicy.GetStateReservoir(Standard);
    public ulong MinRequiredGasLimit => Math.Max(StandardGas, TGasPolicy.GetRemainingGas(FloorGas));

    /// <summary>
    /// EIP-8037: rejects a transaction whose intrinsic execution or floor gas exceeds <paramref name="cap"/>.
    /// </summary>
    public bool ExceedsCap(ulong cap, out ulong execution, out ulong floor)
    {
        TGasPolicy standard = Standard;
        TGasPolicy floorGas = FloorGas;
        execution = TGasPolicy.GetRemainingGas(in standard);
        floor = TGasPolicy.GetRemainingGas(in floorGas);
        return execution > cap || floor > cap;
    }
}
