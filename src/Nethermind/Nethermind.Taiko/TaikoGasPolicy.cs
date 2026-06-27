// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Taiko;

/// <summary>
/// Taiko gas policy: identical to <see cref="EthereumGasPolicy"/> for all EVM/EIP-8037 accounting,
/// plus <see cref="ConsumeL1Gas"/> for charging the L1 data/proving gas a Taiko context-aware
/// precompile reports after execution.
/// </summary>
/// <remarks>
/// Composes (embeds) <see cref="EthereumGasPolicy"/> and forwards every <see cref="IGasPolicy{TSelf}"/>
/// member to it — Taiko's gas schedule is L1-identical, so only the L1-gas charge differs. This is the
/// same composition shape downstream <c>ArbitrumGasPolicy</c> uses.
/// </remarks>
public struct TaikoGasPolicy : IGasPolicy<TaikoGasPolicy>
{
    private EthereumGasPolicy _eth;

    /// <summary>Charge L1 gas (the Taiko precompile's execution-derived consumption). Taiko-specific —
    /// not part of <see cref="IGasPolicy{TSelf}"/>; called directly by the (concrete) Taiko VM.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeL1Gas(ref TaikoGasPolicy gas, ulong gasConsumed) => EthereumGasPolicy.UpdateGas(ref gas._eth, gasConsumed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TaikoGasPolicy FromULong(ulong value) => new() { _eth = EthereumGasPolicy.FromULong(value) };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TaikoGasPolicy CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) => new() { _eth = EthereumGasPolicy.CreateSystemTransactionIntrinsicGas(blockGasLimit) };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TaikoGasPolicy CreateSystemTransactionAvailableGas(ulong gasLimit, in TaikoGasPolicy intrinsicGas, IReleaseSpec spec) => new() { _eth = EthereumGasPolicy.CreateSystemTransactionAvailableGas(gasLimit, in intrinsicGas._eth, spec) };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRemainingGas(in TaikoGasPolicy gas) => EthereumGasPolicy.GetRemainingGas(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetStorageSetStateCost(in TaikoGasPolicy gas) => EthereumGasPolicy.GetStorageSetStateCost(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetCreateStateCost(in TaikoGasPolicy gas) => EthereumGasPolicy.GetCreateStateCost(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetNewAccountStateCost(in TaikoGasPolicy gas) => EthereumGasPolicy.GetNewAccountStateCost(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetPerAuthBaseStateCost(in TaikoGasPolicy gas) => EthereumGasPolicy.GetPerAuthBaseStateCost(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetCodeDepositStateCost(in TaikoGasPolicy gas, int byteCodeLength) => EthereumGasPolicy.GetCodeDepositStateCost(in gas._eth, byteCodeLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetStateReservoir(in TaikoGasPolicy gas) => EthereumGasPolicy.GetStateReservoir(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetStateGasUsed(in TaikoGasPolicy gas) => EthereumGasPolicy.GetStateGasUsed(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Consume(ref TaikoGasPolicy gas, ulong cost) => EthereumGasPolicy.Consume(ref gas._eth, cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStateGas(ref TaikoGasPolicy gas, ulong stateGasCost) => EthereumGasPolicy.ConsumeStateGas(ref gas._eth, stateGasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeStateAndRegularGas(ref TaikoGasPolicy gas, ulong stateGasCost, ulong regularGasCost) => EthereumGasPolicy.TryConsumeStateAndRegularGas(ref gas._eth, stateGasCost, regularGasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeSelfDestructGas(ref TaikoGasPolicy gas) => EthereumGasPolicy.ConsumeSelfDestructGas(ref gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Refund(ref TaikoGasPolicy gas, in TaikoGasPolicy childGas) => EthereumGasPolicy.Refund(ref gas._eth, in childGas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGas(ref TaikoGasPolicy parentGas, in TaikoGasPolicy childGas) => EthereumGasPolicy.RestoreChildStateGas(ref parentGas._eth, in childGas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGasOnHalt(ref TaikoGasPolicy parentGas, in TaikoGasPolicy childGas) => EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parentGas._eth, in childGas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RevertRefundToHalt(ref TaikoGasPolicy parentGas, in TaikoGasPolicy childGas) => EthereumGasPolicy.RevertRefundToHalt(ref parentGas._eth, in childGas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOutOfGas(in TaikoGasPolicy gas) => EthereumGasPolicy.IsOutOfGas(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref TaikoGasPolicy gas) => EthereumGasPolicy.SetOutOfGas(ref gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeAccountAccessGasWithDelegation(ref TaikoGasPolicy gas, IReleaseSpec spec, ref readonly StackAccessTracker accessTracker, bool isTracingAccess, Address address, Address? delegated) =>
        EthereumGasPolicy.ConsumeAccountAccessGasWithDelegation(ref gas._eth, spec, in accessTracker, isTracingAccess, address, delegated);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeAccountAccessGas(ref TaikoGasPolicy gas, IReleaseSpec spec, ref readonly StackAccessTracker accessTracker, bool isTracingAccess, Address address, AccountAccessKind kind = AccountAccessKind.Default) =>
        EthereumGasPolicy.ConsumeAccountAccessGas(ref gas._eth, spec, in accessTracker, isTracingAccess, address, kind);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStorageAccessGas(ref TaikoGasPolicy gas, ref readonly StackAccessTracker accessTracker, bool isTracingAccess, in StorageCell storageCell, StorageAccessType storageAccessType, IReleaseSpec spec) =>
        EthereumGasPolicy.ConsumeStorageAccessGas(ref gas._eth, in accessTracker, isTracingAccess, in storageCell, storageAccessType, spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref TaikoGasPolicy gas, in UInt256 position, in UInt256 length, VmState<TaikoGasPolicy> vmState)
    {
        ulong memoryCost = vmState.Memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost == 0L) return !outOfGas;
        return UpdateGas(ref gas, memoryCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(ref TaikoGasPolicy gas, ulong gasCost) => EthereumGasPolicy.UpdateGas(ref gas._eth, gasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref TaikoGasPolicy gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
        => EthereumGasPolicy.ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref gas._eth, spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref TaikoGasPolicy gas, ulong refund) => EthereumGasPolicy.UpdateGasUp(ref gas._eth, refund);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref TaikoGasPolicy gas, ulong amount, ulong stateGasFloor) => EthereumGasPolicy.RefundStateGas(ref gas._eth, amount, stateGasFloor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref TaikoGasPolicy gas, ulong amount, ulong stateGasFloor, bool trackSpillRefund) => EthereumGasPolicy.RefundStateGas(ref gas._eth, amount, stateGasFloor, trackSpillRefund);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong DiscardStateGas(ref TaikoGasPolicy gas, ulong amount, ulong stateGasFloor, bool trackSpillRefund) => EthereumGasPolicy.DiscardStateGas(ref gas._eth, amount, stateGasFloor, trackSpillRefund);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddStateGasRefundToReservoir(ref TaikoGasPolicy gas, ulong amount, bool trackSpillRefund) => EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas._eth, amount, trackSpillRefund);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RemoveStateGasRefundFromReservoir(ref TaikoGasPolicy gas, ulong amount) => EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas._eth, amount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ResetForHalt(ref TaikoGasPolicy gas, ulong initialStateReservoir, ulong initialStateGasUsed) => EthereumGasPolicy.ResetForHalt(ref gas._eth, initialStateReservoir, initialStateGasUsed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ApplyCodeInsertRefunds(ref TaikoGasPolicy gas, ulong codeInsertRefunds, IReleaseSpec spec, ulong stateGasFloor) => EthereumGasPolicy.ApplyCodeInsertRefunds(ref gas._eth, codeInsertRefunds, spec, stateGasFloor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeCallValueTransfer(ref TaikoGasPolicy gas) => EthereumGasPolicy.ConsumeCallValueTransfer(ref gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeNewAccountCreation<TEip8037>(ref TaikoGasPolicy gas) where TEip8037 : struct, IFlag => EthereumGasPolicy.ConsumeNewAccountCreation<TEip8037>(ref gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeLogEmission(ref TaikoGasPolicy gas, ulong topicCount, ulong dataSize) => EthereumGasPolicy.ConsumeLogEmission(ref gas._eth, topicCount, dataSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeDataCopyGas(ref TaikoGasPolicy gas, IReleaseSpec spec, bool isExternalCode, ulong words) => EthereumGasPolicy.ConsumeDataCopyGas(ref gas._eth, spec, isExternalCode, words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnBeforeInstructionTrace(in TaikoGasPolicy gas, int pc, Instruction instruction, int depth) => EthereumGasPolicy.OnBeforeInstructionTrace(in gas._eth, pc, instruction, depth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnAfterInstructionTrace(in TaikoGasPolicy gas) => EthereumGasPolicy.OnAfterInstructionTrace(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TaikoGasPolicy Max(in TaikoGasPolicy a, in TaikoGasPolicy b) => new() { _eth = EthereumGasPolicy.Max(in a._eth, in b._eth) };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ComputeBlockRegularGas(in TaikoGasPolicy gas, in TaikoGasPolicy intrinsic, ulong txGasLimit, ulong floorGas, ulong remainingRegularGas) =>
        EthereumGasPolicy.ComputeBlockRegularGas(in gas._eth, in intrinsic._eth, txGasLimit, floorGas, remainingRegularGas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ComputeRefundedCreateStateSpillForHalt(in TaikoGasPolicy gas) => EthereumGasPolicy.ComputeRefundedCreateStateSpillForHalt(in gas._eth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (ulong spentGas, ulong blockGas, ulong blockStateGas) ComputeHaltGas(in TaikoGasPolicy gas, ulong txGasLimit, ulong floorGas, ulong refundedCreateStateSpillForHalt) =>
        EthereumGasPolicy.ComputeHaltGas(in gas._eth, txGasLimit, floorGas, refundedCreateStateSpillForHalt);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TaikoGasPolicy CreateChildFrameGas(ref TaikoGasPolicy parentGas, ulong childRegularGas) => new() { _eth = EthereumGasPolicy.CreateChildFrameGas(ref parentGas._eth, childRegularGas) };

    public static IntrinsicGas<TaikoGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
    {
        IntrinsicGas<EthereumGasPolicy> eth = EthereumGasPolicy.CalculateIntrinsicGas(tx, spec, blockGasLimit);
        return new IntrinsicGas<TaikoGasPolicy>(new() { _eth = eth.Standard }, new() { _eth = eth.FloorGas });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TaikoGasPolicy CreateAvailableFromIntrinsic(ulong gasLimit, in TaikoGasPolicy intrinsicGas, IReleaseSpec spec) => new() { _eth = EthereumGasPolicy.CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas._eth, spec) };
}
