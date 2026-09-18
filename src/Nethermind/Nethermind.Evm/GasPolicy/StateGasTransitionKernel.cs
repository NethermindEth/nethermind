// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

internal readonly struct StateGasTransitionResult(
    ulong value,
    long stateReservoir,
    long stateGasUsed,
    long stateGasSpill,
    long stateGasSpillRefunded,
    long unappliedAmount)
{
    public readonly ulong Value = value;
    public readonly long StateReservoir = stateReservoir;
    public readonly long StateGasUsed = stateGasUsed;
    public readonly long StateGasSpill = stateGasSpill;
    public readonly long StateGasSpillRefunded = stateGasSpillRefunded;
    public readonly long UnappliedAmount = unappliedAmount;
}

internal static class StateGasTransitionKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult Refund(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        ulong childValue,
        long childStateReservoir,
        long childStateGasUsed,
        long childStateGasSpill,
        long childStateGasSpillRefunded) =>
        new(
            unchecked(value + childValue),
            unchecked(stateReservoir + childStateReservoir),
            unchecked(stateGasUsed + childStateGasUsed),
            unchecked(stateGasSpill + childStateGasSpill),
            unchecked(stateGasSpillRefunded + childStateGasSpillRefunded),
            0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult RepayStateGasSpill(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded)
    {
        long repayment = Math.Min(stateReservoir, GetUnrefundedStateGasSpill(stateGasSpill, stateGasSpillRefunded));
        return repayment <= 0
            ? new StateGasTransitionResult(value, stateReservoir, stateGasUsed, stateGasSpill, stateGasSpillRefunded, 0)
            : new StateGasTransitionResult(
                unchecked(value + (ulong)repayment),
                unchecked(stateReservoir - repayment),
                stateGasUsed,
                stateGasSpill,
                unchecked(stateGasSpillRefunded + repayment),
                0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult RestoreChildStateGas(
        ulong parentValue,
        long parentStateReservoir,
        long parentStateGasUsed,
        long parentStateGasSpill,
        long parentStateGasSpillRefunded,
        long childStateReservoir,
        long childStateGasUsed,
        long childStateGasSpill,
        long childStateGasSpillRefunded)
    {
        long childNetSpill = GetUnrefundedStateGasSpill(childStateGasSpill, childStateGasSpillRefunded);
        return new StateGasTransitionResult(
            unchecked(parentValue + (ulong)childNetSpill),
            unchecked(unchecked(unchecked(parentStateReservoir + childStateReservoir) + childStateGasUsed) - childNetSpill),
            parentStateGasUsed,
            parentStateGasSpill,
            parentStateGasSpillRefunded,
            0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult RestoreChildStateGasOnHalt(
        ulong parentValue,
        long parentStateReservoir,
        long parentStateGasUsed,
        long parentStateGasSpill,
        long parentStateGasSpillRefunded,
        long childStateReservoir,
        long childStateGasUsed,
        long childStateGasSpill,
        long childStateGasSpillRefunded)
    {
        long childNetSpill = GetUnrefundedStateGasSpill(childStateGasSpill, childStateGasSpillRefunded);
        return new StateGasTransitionResult(
            parentValue,
            unchecked(unchecked(unchecked(parentStateReservoir + childStateReservoir) + childStateGasUsed) - childNetSpill),
            parentStateGasUsed,
            parentStateGasSpill,
            parentStateGasSpillRefunded,
            0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult RevertRefundToHalt(
        ulong parentValue,
        long parentStateReservoir,
        long parentStateGasUsed,
        long parentStateGasSpill,
        long parentStateGasSpillRefunded,
        long childStateGasUsed,
        long childStateGasSpill,
        long childStateGasSpillRefunded)
    {
        long childNetSpill = GetUnrefundedStateGasSpill(childStateGasSpill, childStateGasSpillRefunded);
        return new StateGasTransitionResult(
            parentValue,
            unchecked(unchecked(parentStateReservoir + childStateGasUsed) - childNetSpill),
            unchecked(parentStateGasUsed - childStateGasUsed),
            unchecked(parentStateGasSpill - childStateGasSpill),
            unchecked(parentStateGasSpillRefunded - childStateGasSpillRefunded),
            0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult RefundStateGas(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount,
        long stateGasFloor,
        bool trackSpillRefund)
    {
        long refundableStateGas = PositivePart(unchecked(stateGasUsed - stateGasFloor));
        long appliedRefund = Math.Min(amount, refundableStateGas);
        long toGasLeft = trackSpillRefund
            ? Math.Min(appliedRefund, GetUnrefundedStateGasSpill(stateGasSpill, stateGasSpillRefunded))
            : 0;

        return new StateGasTransitionResult(
            unchecked(value + (ulong)toGasLeft),
            unchecked(stateReservoir + unchecked(appliedRefund - toGasLeft)),
            unchecked(stateGasUsed - appliedRefund),
            stateGasSpill,
            trackSpillRefund ? unchecked(stateGasSpillRefunded + toGasLeft) : stateGasSpillRefunded,
            0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult DiscardStateGas(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount,
        long stateGasFloor)
    {
        long discardableStateGas = PositivePart(unchecked(stateGasUsed - stateGasFloor));
        long appliedRefund = Math.Min(amount, discardableStateGas);
        return new StateGasTransitionResult(
            value,
            stateReservoir,
            unchecked(stateGasUsed - appliedRefund),
            stateGasSpill,
            stateGasSpillRefunded,
            unchecked(amount - appliedRefund));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult AddStateGasRefundToReservoir(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount,
        bool trackSpillRefund)
    {
        long toGasLeft = trackSpillRefund
            ? Math.Min(amount, GetUnrefundedStateGasSpill(stateGasSpill, stateGasSpillRefunded))
            : 0;
        return new StateGasTransitionResult(
            unchecked(value + (ulong)toGasLeft),
            unchecked(stateReservoir + unchecked(amount - toGasLeft)),
            stateGasUsed,
            stateGasSpill,
            trackSpillRefund ? unchecked(stateGasSpillRefunded + toGasLeft) : stateGasSpillRefunded,
            0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionResult RemoveStateGasRefundFromReservoir(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount)
    {
        long fromReservoir = ClampToRefundAmount(stateReservoir, amount);
        long remainingAmount = unchecked(amount - fromReservoir);
        long nextStateReservoir = unchecked(stateReservoir - fromReservoir);
        if (remainingAmount <= 0)
        {
            return new StateGasTransitionResult(
                value,
                nextStateReservoir,
                stateGasUsed,
                stateGasSpill,
                stateGasSpillRefunded,
                0);
        }

        long fromUsed = Math.Min(remainingAmount, stateGasUsed);
        return new StateGasTransitionResult(
            value,
            unchecked(nextStateReservoir - unchecked(remainingAmount - fromUsed)),
            unchecked(stateGasUsed - fromUsed),
            stateGasSpill,
            stateGasSpillRefunded,
            0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetUnrefundedStateGasSpill(long stateGasSpill, long stateGasSpillRefunded)
    {
        long unrefundedSpill = unchecked(stateGasSpill - stateGasSpillRefunded);
        return PositivePart(unrefundedSpill);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PositivePart(long value) => value > 0 ? value : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ClampToRefundAmount(long stateReservoir, long amount) =>
        stateReservoir <= 0 ? 0 : stateReservoir >= amount ? amount : stateReservoir;
}
