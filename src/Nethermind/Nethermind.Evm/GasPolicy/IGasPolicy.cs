// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    static abstract TSelf FromULong(ulong value);

    static virtual TSelf CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) => TSelf.FromULong(0);

    static virtual TSelf CreateSystemTransactionAvailableGas(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec) =>
        TSelf.CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec);

    static abstract ulong GetRemainingGas(in TSelf gas);

    static virtual ulong CombineBlockGas(ulong blockRegularGas, ulong blockStateGas) => Math.Max(blockRegularGas, blockStateGas);

    static virtual ulong ComputeBlockRegularGas(in TSelf gas, in TSelf intrinsic, ulong txGasLimit, ulong floorGas, ulong remainingRegularGas)
    {
        ulong intrinsicRegularGas = TSelf.GetRemainingGas(in intrinsic);
        ulong intrinsicStateGas = TSelf.GetStateReservoir(in intrinsic);
        ulong initialReservoir = Eip8037BlockGasInclusionCheck.CalculateInitialStateReservoir(txGasLimit, intrinsicStateGas);
        ulong totalSub = intrinsicRegularGas + intrinsicStateGas + initialReservoir;
        ulong initialRegularGas = txGasLimit.SaturatingSub(totalSub);
        return Eip8037BlockGasInclusionCheck.CalculateBlockRegularGas(
            intrinsicRegularGas, initialRegularGas, remainingRegularGas, TSelf.GetStateGasSpill(in gas), floorGas);
    }

    static virtual ulong ComputeRefundedCreateStateSpillForHalt(in TSelf gas) => 0;

    static virtual (ulong spentGas, ulong blockGas, ulong blockStateGas) ComputeHaltGas(in TSelf gas, ulong txGasLimit, ulong floorGas, ulong refundedCreateStateSpillForHalt)
    {
        ulong spentGas = Math.Max(txGasLimit, floorGas);
        return (spentGas, spentGas, 0);
    }

    // EIP-8037 state-cost accessors. Pre-EIP-8037 policies return the constant fallback.
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

    static virtual void Consume<TCost>(ref TSelf gas) where TCost : struct, IGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost);

    static virtual void Consume<TCost>(ref TSelf gas, IReleaseSpec spec) where TCost : struct, ISpecGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost(spec));

    static virtual void ConsumeKeccak(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.Sha3 + GasCostOf.Sha3Word * words);

    static virtual void ConsumeMemoryCopy(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.VeryLow + GasCostOf.VeryLow * words);

    static virtual void ConsumeExpBytes(ref TSelf gas, IReleaseSpec spec, ulong exponentByteSize) =>
        TSelf.Consume(ref gas, spec.GasCosts.ExpByteCost * exponentByteSize);

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

    static virtual bool ConsumeSSetFromCleanGas(ref TSelf gas) =>
        TSelf.UpdateGas(ref gas, GasCostOf.SSet - GasCostOf.SReset);

    static virtual bool ConsumePrecompileGas(ref TSelf gas, IPrecompile precompile, ReadOnlyMemory<byte> inputData, IReleaseSpec spec)
    {
        ulong baseGasCost = precompile.BaseGasCost(spec);
        ulong dataGasCost = precompile.DataGasCost(inputData, spec);
        return baseGasCost <= ulong.MaxValue - dataGasCost && TSelf.UpdateGas(ref gas, baseGasCost + dataGasCost);
    }
    static abstract bool ConsumeSelfDestructGas(ref TSelf gas);
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

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
