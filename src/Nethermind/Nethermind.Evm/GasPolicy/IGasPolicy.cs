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
    static virtual TSelf CreateSystemTransactionAvailableGas(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec) =>
        TSelf.CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec);

    static abstract ulong GetRemainingGas(in TSelf gas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong CombineBlockGas(ulong blockRegularGas, ulong blockStateGas) => Math.Max(blockRegularGas, blockStateGas);

    /// <summary>EIP-8037 pre-refund spent gas: <c>txGasLimit - gas_left - state reservoir</c>.</summary>
    /// <remarks>
    /// Centralizes the regular↔state boundary conversion: the reservoir may be negative (net child
    /// spill) and the ulong wrap still yields the correct signed total, asserted non-negative here.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong GetPreRefundGas(in TSelf gas, ulong txGasLimit)
    {
        Debug.Assert((long)txGasLimit - (long)TSelf.GetRemainingGas(in gas) - TSelf.GetStateReservoir(in gas) >= 0,
            $"Gas invariant violated: remaining ({TSelf.GetRemainingGas(in gas)}) + reservoir ({TSelf.GetStateReservoir(in gas)}) exceeds gasLimit ({txGasLimit}).");
        return txGasLimit - TSelf.GetRemainingGas(in gas) - (ulong)TSelf.GetStateReservoir(in gas);
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
    // Tx-wide cumulative spill paid via gas_left in reverted child frames; never undone.
    // Used by top-level halt to reattribute burned spill from state to regular dimension.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasSpillBurned(in TSelf gas) => 0;

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

    static abstract void Consume(ref TSelf gas, ulong cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsume(ref TSelf gas, ulong cost)
    {
        if (TSelf.GetRemainingGas(in gas) < cost) return false;
        TSelf.Consume(ref gas, cost);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void Consume<TCost>(ref TSelf gas) where TCost : struct, IGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void Consume<TCost>(ref TSelf gas, IReleaseSpec spec) where TCost : struct, ISpecGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost(spec));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ConsumeKeccak(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.Sha3 + GasCostOf.Sha3Word * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ConsumeMemoryCopy(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.VeryLow + GasCostOf.VeryLow * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ConsumeExpBytes(ref TSelf gas, IReleaseSpec spec, ulong exponentByteSize) =>
        TSelf.Consume(ref gas, spec.GasCosts.ExpByteCost * exponentByteSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeCreateGas<TEip8037, TOpCreate>(ref TSelf gas, IReleaseSpec spec, ulong initCodeWords)
        where TEip8037 : struct, IFlag
        where TOpCreate : struct, EvmInstructions.IOpCreate
    {
        ulong baseCost = spec.IsEip8038Enabled ? Eip8038Constants.CreateAccess
            : TEip8037.IsActive ? GasCostOf.CreateRegular
            : GasCostOf.Create;
        ulong initCodeWordCost = spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * initCodeWords : 0;
        ulong create2HashCost = typeof(TOpCreate) == typeof(EvmInstructions.OpCreate2) ? GasCostOf.Sha3Word * initCodeWords : 0;
        return TSelf.UpdateGas(ref gas, baseCost + initCodeWordCost + create2HashCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeCallBaseGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.CallCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeSStoreResetGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.SStoreResetCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeNetMeteredSStoreGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.NetMeteredSStoreCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeSSetFromCleanGas(ref TSelf gas) =>
        TSelf.UpdateGas(ref gas, GasCostOf.SSet - GasCostOf.SReset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumePrecompileGas(ref TSelf gas, IPrecompile precompile, ReadOnlyMemory<byte> inputData, IReleaseSpec spec)
    {
        ulong baseGasCost = precompile.BaseGasCost(spec);
        ulong dataGasCost = precompile.DataGasCost(inputData, spec);
        return baseGasCost <= ulong.MaxValue - dataGasCost && TSelf.UpdateGas(ref gas, baseGasCost + dataGasCost);
    }
    static abstract bool ConsumeSelfDestructGas(ref TSelf gas);
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeCreateStateGas(ref TSelf gas) =>
        TSelf.ConsumeStateGas(ref gas, TSelf.GetCreateStateCost());

    // Revert path: restore the child's state gas into the parent reservoir.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RestoreChildStateGas(ref TSelf parentGas, in TSelf childGas) { }
    // Halt path: preserve inline state-gas refunds (call chain resets to top-most failing call).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RestoreChildStateGasOnHalt(ref TSelf parentGas, in TSelf childGas) { }
    // Code-deposit-failure path: undo prior Refund's state-gas merge and apply halt restoration.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RevertRefundToHalt(ref TSelf parentGas, in TSelf childGas) { }

    static abstract bool IsOutOfGas(in TSelf gas);

    static abstract void SetOutOfGas(ref TSelf gasState);

    static abstract bool ConsumeAccountAccessGasWithDelegation(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        Address? delegated);

    static abstract bool ConsumeAccountAccessGas(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        AccountAccessKind kind = AccountAccessKind.Default);

    static abstract bool ConsumeStorageAccessGas(ref TSelf gas,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec);

    static abstract bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, in UInt256 length, ref EvmPooledMemory memory);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, ulong length, ref EvmPooledMemory memory)
    {
        UInt256 uint256Length = new(length);
        return TSelf.UpdateMemoryCost(ref gas, in position, in uint256Length, ref memory);
    }

    static abstract bool UpdateGas(ref TSelf gas, ulong gasCost);

    // Pre-EIP-8037 fallback: state gas folded into regular gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeStateGas(ref TSelf gas, long stateGasCost) => TSelf.UpdateGas(ref gas, (ulong)stateGasCost);

    // Regular gas charged first to prevent state-gas spill-then-halt from inflating
    // the reservoir via the error refund path.
    static abstract bool TryConsumeStateAndRegularGas(ref TSelf gas, long stateGasCost, ulong regularGasCost);

    static abstract void UpdateGasUp(ref TSelf gas, ulong refund);

    static abstract bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref TSelf gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag;

    // Pre-EIP-8037 fallback: refund into regular gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RefundStateGas(ref TSelf gas, long amount, long stateGasFloor) => TSelf.UpdateGasUp(ref gas, (ulong)amount);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RefundStateGas(ref TSelf gas, long amount, long stateGasFloor, bool trackSpillRefund) =>
        TSelf.RefundStateGas(ref gas, amount, stateGasFloor);

    // Drop state-gas from block-state accounting without refunding to the gas budget;
    // reverted state charges stay paid by the tx but don't contribute to committed state gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long DiscardStateGas(ref TSelf gas, long amount, long stateGasFloor, bool trackSpillRefund) => amount;

    /// <summary>Credits a speculative state-gas refund to the frame's reservoir.</summary>
    /// <returns>The spill-refund amount marked, to be passed back verbatim as
    /// <c>trackedSpillRefund</c> when the advance is revoked.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long AddStateGasRefundToReservoir(ref TSelf gas, long amount, bool trackSpillRefund)
    {
        TSelf.UpdateGasUp(ref gas, (ulong)amount);
        return 0;
    }

    /// <summary>Revokes a speculative refund credited by <see cref="AddStateGasRefundToReservoir"/>.</summary>
    /// <param name="trackedSpillRefund">The value returned by the matching add; unmarks the spill refund exactly.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RemoveStateGasRefundFromReservoir(ref TSelf gas, long amount, long trackedSpillRefund) { }

    // EIP-8037 top-level halt: snap state-gas back to (R0, intrinsicStateUsed, 0); the
    // post-reset StateGasUsed feeds SpentGas so the user doesn't pay for uncommitted state.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ResetForHalt(ref TSelf gas, long initialStateReservoir, long initialStateGasUsed) { }

    // EIP-7702 code-insert refund regular-gas portion. Pre-EIP-8037: (NewAccount - PerAuthBaseCost) each.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong GetCodeInsertRegularRefund(ulong codeInsertRefunds, IReleaseSpec spec) =>
        codeInsertRefunds > 0UL ? (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds : 0UL;

    // EIP-8037: replenishes tx state reservoir before exec (intrinsic state gas already charged).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong ApplyCodeInsertRefunds(ref TSelf gas, ulong codeInsertRefunds, IReleaseSpec spec, long stateGasFloor) =>
        TSelf.GetCodeInsertRegularRefund(codeInsertRefunds, spec);

    static abstract bool ConsumeCallValueTransfer(ref TSelf gas);
    static abstract bool ConsumeCallValueTransferEip2780(ref TSelf gas);
    static abstract bool ConsumeNewAccountCreation<TEip8037>(ref TSelf gas) where TEip8037 : struct, IFlag;
    static abstract bool ConsumeLogEmission(ref TSelf gas, ulong topicCount, ulong dataSize);
    static abstract TSelf Max(in TSelf a, in TSelf b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        TSelf.CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);
    static abstract IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit);

    static abstract TSelf CreateAvailableFromIntrinsic(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual TSelf CreateChildFrameGas(ref TSelf parentGas, ulong childRegularGas) => TSelf.FromULong(childRegularGas);

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
    static abstract void ConsumeDataCopyGas(ref TSelf gas, IReleaseSpec spec, bool isExternalCode, ulong words);

    static abstract void OnBeforeInstructionTrace(in TSelf gas, int pc, Instruction instruction, int depth);
    static abstract void OnAfterInstructionTrace(in TSelf gas);
}

public readonly record struct IntrinsicGas<TGasPolicy>(TGasPolicy Standard, TGasPolicy FloorGas)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public TGasPolicy MinimalGas { get; } = TGasPolicy.Max(Standard, FloorGas);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator TGasPolicy(IntrinsicGas<TGasPolicy> gas) => gas.MinimalGas;

    /// <summary>
    /// EIP-8037: rejects a transaction whose intrinsic regular or floor gas exceeds <paramref name="cap"/>.
    /// </summary>
    public bool ExceedsCap(ulong cap, out ulong regular, out ulong floor)
    {
        TGasPolicy standard = Standard;
        TGasPolicy floorGas = FloorGas;
        regular = TGasPolicy.GetRemainingGas(in standard);
        floor = TGasPolicy.GetRemainingGas(in floorGas);
        return regular > cap || floor > cap;
    }
}
