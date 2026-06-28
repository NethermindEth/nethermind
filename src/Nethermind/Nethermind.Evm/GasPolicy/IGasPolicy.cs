// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Per-chain EVM gas accounting strategy. Implemented by a <c>struct</c> and monomorphized into
/// <see cref="VirtualMachine{TGasPolicy}"/>/<c>TransactionProcessor&lt;TGasPolicy&gt;</c> so every charge
/// dispatches with zero overhead (devirtualized + const-folded) on the per-opcode hot path.
/// </summary>
/// <remarks>
/// Design invariants (do not re-litigate — these were established empirically against the EVM Opcode
/// Benchmark and are enforced by tests):
/// <list type="bullet">
/// <item>The implementing struct must hold ONLY flat top-level scalar fields. A vector / <c>[InlineArray]</c> /
/// SIMD / nested-struct field address-exposes the struct (dotnet/runtime#110968) and defeats JIT
/// enregistration, regressing every opcode. "Vector gas" is shelved; guarded by a layout test.</item>
/// <item>Multidimensional ("multigas") accounting is modelled as additional flat scalar fields routed by
/// compile-time cost/dimension tags, never an indexed runtime vector on the live budget. Any vector/SIMD
/// representation belongs only on the cold per-tx/per-block accounting layer.</item>
/// <item>Charges carry no precomputed number from the opcode: fixed costs use the compile-time
/// <see cref="IGasCost"/>/<see cref="ISpecGasCost"/> tags, dynamic costs use named policy methods.</item>
/// <item>The policy is WorldState/VmState/refund/tracer-free; those concerns stay in the VM/processor.</item>
/// </list>
/// </remarks>
public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    static abstract TSelf FromULong(ulong value);

    static virtual TSelf CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) => TSelf.FromULong(0);

    static virtual TSelf CreateSystemTransactionAvailableGas(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec) =>
        TSelf.CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec);

    static abstract ulong GetRemainingGas(in TSelf gas);

    // Block-gas combination rule. The two-dimensional regular/state gas is reduced to the single
    // header GasUsed by taking the per-dimension max — a block is full when its bottleneck resource
    // is full (EIP-8037). Summing/instrumentation policies (e.g. Arbitrum multigas) override to add.
    // For single-dimensional policies blockStateGas is 0, so max(regular, 0) == regular.
    static virtual ulong CombineBlockGas(ulong blockRegularGas, ulong blockStateGas) => Math.Max(blockRegularGas, blockStateGas);

    // EIP-8037 regular-dimension block gas at tx end. Computed inside the policy so its spill
    // bookkeeping stays private; the default models a single-dimensional policy (no spill).
    static virtual ulong ComputeBlockRegularGas(in TSelf gas, in TSelf intrinsic, ulong txGasLimit, ulong floorGas, ulong remainingRegularGas)
    {
        ulong intrinsicRegularGas = TSelf.GetRemainingGas(in intrinsic);
        ulong intrinsicStateGas = TSelf.GetStateReservoir(in intrinsic);
        ulong totalCap = intrinsicStateGas + Eip7825Constants.DefaultTxGasLimitCap;
        ulong initialReservoir = txGasLimit.SaturatingSub(totalCap);
        ulong totalSub = intrinsicRegularGas + intrinsicStateGas + initialReservoir;
        ulong initialRegularGas = txGasLimit.SaturatingSub(totalSub);
        return Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
            intrinsicRegularGas, initialRegularGas, remainingRegularGas, TSelf.GetStateGasSpill(in gas), floorGas);
    }

    // EIP-8037 top-level-halt spill reattribution: whole CREATE-state units whose spill was refunded
    // are restored to the state dimension. Default 0 — single-dimensional policies have no spill.
    static virtual ulong ComputeRefundedCreateStateSpillForHalt(in TSelf gas) => 0;

    // EIP-8037 top-level-halt gas finalization: (user spent gas, block regular gas, block state gas).
    // Default models a single-dimensional policy (no reservoir / state gas).
    static virtual (ulong spentGas, ulong blockGas, ulong blockStateGas) ComputeHaltGas(in TSelf gas, ulong txGasLimit, ulong floorGas, ulong refundedCreateStateSpillForHalt)
    {
        ulong spentGas = Math.Max(txGasLimit, floorGas);
        return (spentGas, spentGas, 0);
    }

    // EIP-8037 state-cost accessors. Pre-EIP-8037 policies return the constant fallback;
    // a repricing policy overrides these to charge chain-specific state costs.
    static virtual ulong GetStorageSetStateCost() => GasCostOf.SSetState;
    static virtual ulong GetCreateStateCost() => GasCostOf.CreateState;
    static virtual ulong GetNewAccountStateCost() => GasCostOf.NewAccountState;
    static virtual ulong GetPerAuthBaseStateCost() => GasCostOf.PerAuthBaseState;
    static virtual ulong GetCodeDepositStateCost(int byteCodeLength) => GasCostOf.CodeDepositState * (ulong)byteCodeLength;

    // EIP-8037 state-accounting accessors. Pre-EIP-8037 policies return 0.
    static virtual ulong GetStateReservoir(in TSelf gas) => 0;
    static virtual ulong GetStateGasUsed(in TSelf gas) => 0;
    static virtual ulong GetStateGasSpill(in TSelf gas) => 0;

    static abstract void Consume(ref TSelf gas, ulong cost);

    static virtual bool TryConsume(ref TSelf gas, ulong cost)
    {
        if (TSelf.GetRemainingGas(in gas) < cost) return false;
        TSelf.Consume(ref gas, cost);
        return true;
    }

    // Charge a fixed opcode cost via a compile-time tag: TCost.GasCost const-folds (monomorphized),
    // so the caller passes no precomputed number.
    static virtual void Consume<TCost>(ref TSelf gas) where TCost : struct, IGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost);

    // Spec-dependent fixed charge: the cost is read from the price book (spec) inside the policy.
    static virtual void Consume<TCost>(ref TSelf gas, IReleaseSpec spec) where TCost : struct, ISpecGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost(spec));

    // Dynamic per-word charges — the caller passes the word count (the data), the policy owns the
    // base + per-word cost formula. Mirrors ConsumeDataCopyGas.
    static virtual void ConsumeKeccak(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.Sha3 + GasCostOf.Sha3Word * words);

    static virtual void ConsumeMemoryCopy(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.VeryLow + GasCostOf.VeryLow * words);

    // EXP per-byte charge: caller passes the exponent's significant byte length; cost from the spec.
    static virtual void ConsumeExpBytes(ref TSelf gas, IReleaseSpec spec, ulong exponentByteSize) =>
        TSelf.Consume(ref gas, spec.GasCosts.ExpByteCost * exponentByteSize);

    // Opcode base-cost charges (return false on OOG). The amount is produced inside the policy from the
    // spec price book, fork/opcode flags, and opcode-extracted scalars. Selecting which charge applies
    // (e.g. the SSTORE net-metering decision, which reads world state) stays in the opcode.

    // CREATE/CREATE2 base cost: fixed base + EIP-3860 per-init-word + (CREATE2 only) keccak-per-word.
    static virtual bool ConsumeCreateGas<TEip8037, TOpCreate>(ref TSelf gas, IReleaseSpec spec, ulong initCodeWords)
        where TEip8037 : struct, IFlag
        where TOpCreate : struct, EvmInstructions.IOpCreate
    {
        ulong baseCost = TEip8037.IsActive ? GasCostOf.CreateRegular : GasCostOf.Create;
        ulong initCodeWordCost = spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * initCodeWords : 0;
        ulong create2HashCost = typeof(TOpCreate) == typeof(EvmInstructions.OpCreate2) ? GasCostOf.Sha3Word * initCodeWords : 0;
        return TSelf.UpdateGas(ref gas, baseCost + initCodeWordCost + create2HashCost);
    }

    static virtual bool ConsumeCallBaseGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.CallCost);

    static virtual bool ConsumeSStoreResetGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.SStoreResetCost);

    static virtual bool ConsumeNetMeteredSStoreGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.NetMeteredSStoreCost);

    // SSTORE first non-zero write over a clean zero slot: the SSet/SReset delta on top of the reset already charged.
    static virtual bool ConsumeSSetFromCleanGas(ref TSelf gas) =>
        TSelf.UpdateGas(ref gas, GasCostOf.SSet - GasCostOf.SReset);

    // Precompile charge: the caller passes the precompile + input + spec (the data needed to price it);
    // the policy reads the precompile's own base/data cost formulas. Returns false on OOG (incl. the
    // overflow guard before summing), without charging on overflow — matching the prior inline guard.
    static virtual bool ConsumePrecompileGas(ref TSelf gas, IPrecompile precompile, ReadOnlyMemory<byte> inputData, IReleaseSpec spec)
    {
        ulong baseGasCost = precompile.BaseGasCost(spec);
        ulong dataGasCost = precompile.DataGasCost(inputData, spec);
        return baseGasCost <= ulong.MaxValue - dataGasCost && TSelf.UpdateGas(ref gas, baseGasCost + dataGasCost);
    }
    static abstract bool ConsumeSelfDestructGas(ref TSelf gas);
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

    // CREATE state-gas charge (EIP-8037): the policy reads its own CreateState cost, no number passed.
    static virtual bool ConsumeCreateStateGas(ref TSelf gas) =>
        TSelf.ConsumeStateGas(ref gas, TSelf.GetCreateStateCost());

    // Revert path: restore the child's state gas into the parent reservoir.
    static virtual void RestoreChildStateGas(ref TSelf parentGas, in TSelf childGas) { }
    // Halt path: preserve inline state-gas refunds (call chain resets to top-most failing call).
    static virtual void RestoreChildStateGasOnHalt(ref TSelf parentGas, in TSelf childGas) { }
    // Code-deposit-failure path: undo prior Refund's state-gas merge and apply halt restoration.
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

    static virtual bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, ulong length, ref EvmPooledMemory memory)
    {
        UInt256 uint256Length = new(length);
        return TSelf.UpdateMemoryCost(ref gas, in position, in uint256Length, ref memory);
    }

    static abstract bool UpdateGas(ref TSelf gas, ulong gasCost);

    // Pre-EIP-8037 fallback: state gas folded into regular gas.
    static virtual bool ConsumeStateGas(ref TSelf gas, ulong stateGasCost) => TSelf.UpdateGas(ref gas, stateGasCost);

    // Regular gas charged first to prevent state-gas spill-then-halt from inflating
    // the reservoir via the error refund path.
    static abstract bool TryConsumeStateAndRegularGas(ref TSelf gas, ulong stateGasCost, ulong regularGasCost);

    static abstract void UpdateGasUp(ref TSelf gas, ulong refund);

    static abstract bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref TSelf gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag;

    // Pre-EIP-8037 fallback: refund into regular gas.
    static virtual void RefundStateGas(ref TSelf gas, ulong amount, ulong stateGasFloor) => TSelf.UpdateGasUp(ref gas, amount);
    static virtual void RefundStateGas(ref TSelf gas, ulong amount, ulong stateGasFloor, bool trackSpillRefund) =>
        TSelf.RefundStateGas(ref gas, amount, stateGasFloor);

    // Drop state-gas from block-state accounting without refunding to the gas budget;
    // reverted state charges stay paid by the tx but don't contribute to committed state gas.
    static virtual ulong DiscardStateGas(ref TSelf gas, ulong amount, ulong stateGasFloor, bool trackSpillRefund) => amount;

    static virtual void AddStateGasRefundToReservoir(ref TSelf gas, ulong amount, bool trackSpillRefund) =>
        TSelf.UpdateGasUp(ref gas, amount);

    static virtual void RemoveStateGasRefundFromReservoir(ref TSelf gas, ulong amount) { }

    // EIP-8037 top-level halt: snap state-gas back to (R0, intrinsicStateUsed, 0); the
    // post-reset StateGasUsed feeds SpentGas so the user doesn't pay for uncommitted state.
    static virtual void ResetForHalt(ref TSelf gas, ulong initialStateReservoir, ulong initialStateGasUsed) { }

    // EIP-8037: replenishes tx state reservoir before exec (intrinsic state gas already charged).
    // Default = the EIP-7702 code-insert regular-gas refund: (NewAccount - PerAuthBaseCost) each.
    static virtual ulong ApplyCodeInsertRefunds(ref TSelf gas, ulong codeInsertRefunds, IReleaseSpec spec, ulong stateGasFloor) =>
        codeInsertRefunds > 0UL ? (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds : 0UL;

    static abstract bool ConsumeCallValueTransfer(ref TSelf gas);
    static abstract bool ConsumeNewAccountCreation<TEip8037>(ref TSelf gas) where TEip8037 : struct, IFlag;
    static abstract bool ConsumeLogEmission(ref TSelf gas, ulong topicCount, ulong dataSize);
    static abstract TSelf Max(in TSelf a, in TSelf b);

    static virtual IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        TSelf.CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);
    static abstract IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit);

    static abstract TSelf CreateAvailableFromIntrinsic(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec);

    static virtual TSelf CreateChildFrameGas(ref TSelf parentGas, ulong childRegularGas) => TSelf.FromULong(childRegularGas);

    // EIP-150: gas forwarded to a child frame is capped at 63/64 of the parent's remaining gas
    // (no cap pre-EIP-150). These charge the forwarded amount from the parent and return it, so the
    // 63/64 rule lives in the policy rather than being recomputed at each call/create site.

    // CALL-style: forward the requested amount, capped to 63/64. Pre-EIP-150 the request must fit ulong.
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

    // CREATE-style: forward all remaining gas (capped to 63/64 under EIP-150).
    static virtual bool TryReserveChildGas(ref TSelf gas, IReleaseSpec spec, out ulong childGas)
    {
        ulong gasAvailable = TSelf.GetRemainingGas(in gas);
        childGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64 : gasAvailable;
        return TSelf.UpdateGas(ref gas, childGas);
    }

    // The policy computes the full data-copy cost (base access cost + per-word copy cost) from the
    // spec and word count; EXTCODECOPY (isExternalCode) may categorize the base as state-trie access.
    static abstract void ConsumeDataCopyGas(ref TSelf gas, IReleaseSpec spec, bool isExternalCode, ulong words);

    static abstract void OnBeforeInstructionTrace(in TSelf gas, int pc, Instruction instruction, int depth);
    static abstract void OnAfterInstructionTrace(in TSelf gas);
}

public readonly record struct IntrinsicGas<TGasPolicy>(TGasPolicy Standard, TGasPolicy FloorGas)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public TGasPolicy MinimalGas { get; } = TGasPolicy.Max(Standard, FloorGas);
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
