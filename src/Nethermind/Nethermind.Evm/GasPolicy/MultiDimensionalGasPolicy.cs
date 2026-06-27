// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Instrumentation gas policy that partitions every charge across <see cref="MultiGasDimension"/>
/// resources while keeping a single spendable <see cref="Remaining"/> budget.
/// </summary>
/// <remarks>
/// Models go-ethereum / Arbitrum-Nitro multi-gas accounting: each operation's existing gas cost is
/// attributed to one resource dimension, and the per-dimension amounts sum back to the legacy
/// single-dimensional cost. <see cref="Remaining"/> mirrors <see cref="EthereumGasPolicy"/>'s
/// <c>Value</c> exactly, so consensus gas is unchanged — the per-dimension <see cref="Used"/> vector
/// is pure instrumentation. This policy targets the legacy single-dimensional model (it does not
/// implement the EIP-8037 state-gas reservoir; state growth is tracked as the
/// <see cref="MultiGasDimension.StorageGrowth"/> dimension that sums into <see cref="Remaining"/>).
/// </remarks>
public struct MultiDimensionalGasPolicy : IGasPolicy<MultiDimensionalGasPolicy>
{
    /// <summary>Spendable single-dimensional gas budget (legacy gas_left).</summary>
    public ulong Remaining;

    /// <summary>Indicates that execution encountered an out of gas condition.</summary>
    public bool OutOfGas;

    // CS0649: _used is written only through the indexer; the compiler does not track an
    // InlineArray-wrapped element write as an assignment to the field itself.
#pragma warning disable CS0649
    private Usage _used;
#pragma warning restore CS0649

    [InlineArray((int)MultiGasDimension.HistoryGrowth + 1)]
    private struct Usage
    {
        private ulong _element0;
    }

    /// <summary>Cumulative gas attributed to <paramref name="dimension"/> so far.</summary>
    public readonly ulong Used(MultiGasDimension dimension) => _used[(int)dimension];

    /// <summary>Charge <paramref name="cost"/> to <paramref name="dimension"/>, failing (and burning
    /// the remaining budget) when it cannot be covered — mirrors <see cref="UpdateGas"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Charge(ref MultiDimensionalGasPolicy gas, MultiGasDimension dimension, ulong cost)
    {
        if (gas.Remaining < cost)
        {
            gas._used[(int)dimension] += gas.Remaining;
            gas.Remaining = 0;
            gas.OutOfGas = true;
            return false;
        }

        gas.Remaining -= cost;
        gas._used[(int)dimension] += cost;
        return true;
    }

    /// <summary>Charge <paramref name="cost"/> to <paramref name="dimension"/>, saturating to the
    /// remaining budget on overflow — mirrors <see cref="Consume"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChargeSaturating(ref MultiDimensionalGasPolicy gas, MultiGasDimension dimension, ulong cost)
    {
        ulong applied = Math.Min(cost, gas.Remaining);
        gas.Remaining -= applied;
        gas._used[(int)dimension] += applied;
        if (applied < cost) gas.OutOfGas = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MultiDimensionalGasPolicy FromULong(ulong value) => new() { Remaining = value };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRemainingGas(in MultiDimensionalGasPolicy gas) => gas.Remaining;

    // Instrumentation multi-gas sums its dimensions back to the legacy single number, so block gas is
    // the sum of the per-dimension totals — contrast EIP-8037's bottleneck max. This is the concrete
    // point where the block combination rule is a policy concern.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CombineBlockGas(ulong blockRegularGas, ulong blockStateGas) => blockRegularGas + blockStateGas;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Consume(ref MultiDimensionalGasPolicy gas, ulong cost) =>
        ChargeSaturating(ref gas, MultiGasDimension.Computation, cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(ref MultiDimensionalGasPolicy gas, ulong gasCost) =>
        Charge(ref gas, MultiGasDimension.Computation, gasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref MultiDimensionalGasPolicy gas, ulong refund)
    {
        gas.Remaining += refund;
        // Best-effort: roll the refund back off the catch-all dimension to keep Used summing to spent gas.
        gas._used[(int)MultiGasDimension.Computation] -= Math.Min(refund, gas._used[(int)MultiGasDimension.Computation]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStateGas(ref MultiDimensionalGasPolicy gas, ulong stateGasCost) =>
        Charge(ref gas, MultiGasDimension.StorageGrowth, stateGasCost);

    public static bool TryConsumeStateAndRegularGas(ref MultiDimensionalGasPolicy gas, ulong stateGasCost, ulong regularGasCost) =>
        (regularGasCost == 0 || Charge(ref gas, MultiGasDimension.Computation, regularGasCost)) &&
        (stateGasCost == 0 || Charge(ref gas, MultiGasDimension.StorageGrowth, stateGasCost));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeSelfDestructGas(ref MultiDimensionalGasPolicy gas) =>
        Charge(ref gas, MultiGasDimension.StorageAccessWrite, GasCostOf.SelfDestructEip150);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeCodeDeposit(ref MultiDimensionalGasPolicy gas, ulong cost) =>
        ChargeSaturating(ref gas, MultiGasDimension.StorageGrowth, cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Refund(ref MultiDimensionalGasPolicy gas, in MultiDimensionalGasPolicy childGas)
    {
        gas.Remaining += childGas.Remaining;
        for (int i = 0; i <= (int)MultiGasDimension.HistoryGrowth; i++)
        {
            gas._used[i] += childGas._used[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOutOfGas(in MultiDimensionalGasPolicy gas) => gas.OutOfGas;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutOfGas(ref MultiDimensionalGasPolicy gas)
    {
        gas._used[(int)MultiGasDimension.Computation] += gas.Remaining;
        gas.Remaining = 0;
        gas.OutOfGas = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeAccountAccessGasWithDelegation(ref MultiDimensionalGasPolicy gas,
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

    public static bool ConsumeAccountAccessGas(ref MultiDimensionalGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        AccountAccessKind kind = AccountAccessKind.Default)
    {
        if (!spec.UseHotAndColdStorage) return true;
        if (isTracingAccess)
        {
            accessTracker.WarmUp(address);
        }

        return (accessTracker.WarmUp(address) && !spec.IsPrecompile(address)) switch
        {
            // Cold access reads existing state; warm access is computation.
            true => Charge(ref gas, MultiGasDimension.StorageAccessRead, GasCostOf.ColdAccountAccess),
            false when kind == AccountAccessKind.SelfDestructBeneficiary => true,
            false => Charge(ref gas, MultiGasDimension.Computation, GasCostOf.WarmStateRead)
        };
    }

    public static bool ConsumeStorageAccessGas(ref MultiDimensionalGasPolicy gas,
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
            return Charge(ref gas, MultiGasDimension.StorageAccessRead, GasCostOf.ColdSLoad);
        if (storageAccessType == StorageAccessType.SLOAD)
            return Charge(ref gas, MultiGasDimension.Computation, GasCostOf.WarmStateRead);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref MultiDimensionalGasPolicy gas,
        in UInt256 position,
        in UInt256 length, VmState<MultiDimensionalGasPolicy> vmState)
    {
        ulong memoryCost = vmState.Memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost == 0)
            return !outOfGas;
        return Charge(ref gas, MultiGasDimension.Computation, memoryCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref MultiDimensionalGasPolicy gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
    {
        // Legacy (non-8037) single-dimensional schedule: slot creation grows state, reset writes existing state.
        if (!TIsSlotCreation.IsActive) return Charge(ref gas, MultiGasDimension.StorageAccessWrite, spec.GasCosts.SStoreResetCost);
        return Charge(ref gas, MultiGasDimension.StorageGrowth, GasCostOf.SSet);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeCallValueTransfer(ref MultiDimensionalGasPolicy gas) =>
        Charge(ref gas, MultiGasDimension.Computation, GasCostOf.CallValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeNewAccountCreation<TEip8037>(ref MultiDimensionalGasPolicy gas) where TEip8037 : struct, IFlag =>
        Charge(ref gas, MultiGasDimension.StorageGrowth, GasCostOf.NewAccount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ConsumeLogEmission(ref MultiDimensionalGasPolicy gas, ulong topicCount, ulong dataSize)
    {
        ulong cost = GasCostOf.Log + topicCount * GasCostOf.LogTopic + dataSize * GasCostOf.LogData;
        return Charge(ref gas, MultiGasDimension.HistoryGrowth, cost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ConsumeDataCopyGas(ref MultiDimensionalGasPolicy gas, IReleaseSpec spec, bool isExternalCode, ulong words)
    {
        ulong baseCost = isExternalCode ? spec.GasCosts.ExtCodeCost : GasCostOf.VeryLow;
        ChargeSaturating(ref gas, isExternalCode ? MultiGasDimension.StorageAccessRead : MultiGasDimension.Computation, baseCost);
        ChargeSaturating(ref gas, MultiGasDimension.Computation, GasCostOf.Memory * words);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnBeforeInstructionTrace(in MultiDimensionalGasPolicy gas, int pc, Instruction instruction, int depth) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnAfterInstructionTrace(in MultiDimensionalGasPolicy gas) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MultiDimensionalGasPolicy Max(in MultiDimensionalGasPolicy a, in MultiDimensionalGasPolicy b) =>
        a.Remaining >= b.Remaining ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntrinsicGas<MultiDimensionalGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);

    public static IntrinsicGas<MultiDimensionalGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
    {
        ulong tokensInCallData = IGasPolicy<MultiDimensionalGasPolicy>.CalculateTokensInCallData(tx, spec);
        ulong floorTokensInAccessList = IGasPolicy<MultiDimensionalGasPolicy>.CalculateFloorTokensInAccessList(tx, spec);
        (ulong authRegularCost, _) = IGasPolicy<MultiDimensionalGasPolicy>.AuthorizationListCost(tx, spec);
        ulong accessListCost = IGasPolicy<MultiDimensionalGasPolicy>.AccessListCost(tx, spec, floorTokensInAccessList);

        ulong regularGas = GasCostOf.Transaction
                           + spec.GetBaseDataCost(tx) + tokensInCallData * GasCostOf.TxDataZero
                           + (tx.IsContractCreation && spec.IsEip2Enabled ? GasCostOf.TxCreate : 0)
                           + accessListCost
                           + authRegularCost;
        ulong floorCost = IGasPolicy<MultiDimensionalGasPolicy>.CalculateFloorCost(tx, spec, tokensInCallData, floorTokensInAccessList);
        return new IntrinsicGas<MultiDimensionalGasPolicy>(FromULong(regularGas), FromULong(floorCost));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MultiDimensionalGasPolicy CreateAvailableFromIntrinsic(ulong gasLimit, in MultiDimensionalGasPolicy intrinsicGas, IReleaseSpec spec) =>
        FromULong(gasLimit - intrinsicGas.Remaining);
}
