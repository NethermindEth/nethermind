// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

internal enum StateGasTransitionAdapterOutcomeKind : byte
{
    CompletedVoid,
    CompletedDiscard,
    ArgumentException,
}

internal readonly struct StateGasTransitionAdapterOutcome(
    StateGasTransitionAdapterOutcomeKind kind,
    StateGasTransitionResult transition)
{
    public readonly StateGasTransitionAdapterOutcomeKind Kind = kind;
    public readonly StateGasTransitionResult Transition = transition;
}

internal static class StateGasTransitionAdapterKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome Refund(
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
            StateGasTransitionAdapterOutcomeKind.CompletedVoid,
            StateGasTransitionKernel.Refund(
                value,
                stateReservoir,
                stateGasUsed,
                stateGasSpill,
                stateGasSpillRefunded,
                childValue,
                childStateReservoir,
                childStateGasUsed,
                childStateGasSpill,
                childStateGasSpillRefunded));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome RepayStateGasSpill(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded) =>
        new(
            StateGasTransitionAdapterOutcomeKind.CompletedVoid,
            StateGasTransitionKernel.RepayStateGasSpill(
                value,
                stateReservoir,
                stateGasUsed,
                stateGasSpill,
                stateGasSpillRefunded));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome RestoreChildStateGas(
        ulong parentValue,
        long parentStateReservoir,
        long parentStateGasUsed,
        long parentStateGasSpill,
        long parentStateGasSpillRefunded,
        long childStateReservoir,
        long childStateGasUsed,
        long childStateGasSpill,
        long childStateGasSpillRefunded) =>
        new(
            StateGasTransitionAdapterOutcomeKind.CompletedVoid,
            StateGasTransitionKernel.RestoreChildStateGas(
                parentValue,
                parentStateReservoir,
                parentStateGasUsed,
                parentStateGasSpill,
                parentStateGasSpillRefunded,
                childStateReservoir,
                childStateGasUsed,
                childStateGasSpill,
                childStateGasSpillRefunded));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome RestoreChildStateGasOnHalt(
        ulong parentValue,
        long parentStateReservoir,
        long parentStateGasUsed,
        long parentStateGasSpill,
        long parentStateGasSpillRefunded,
        long childStateReservoir,
        long childStateGasUsed,
        long childStateGasSpill,
        long childStateGasSpillRefunded) =>
        new(
            StateGasTransitionAdapterOutcomeKind.CompletedVoid,
            StateGasTransitionKernel.RestoreChildStateGasOnHalt(
                parentValue,
                parentStateReservoir,
                parentStateGasUsed,
                parentStateGasSpill,
                parentStateGasSpillRefunded,
                childStateReservoir,
                childStateGasUsed,
                childStateGasSpill,
                childStateGasSpillRefunded));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome RevertRefundToHalt(
        ulong parentValue,
        long parentStateReservoir,
        long parentStateGasUsed,
        long parentStateGasSpill,
        long parentStateGasSpillRefunded,
        long childStateGasUsed,
        long childStateGasSpill,
        long childStateGasSpillRefunded) =>
        new(
            StateGasTransitionAdapterOutcomeKind.CompletedVoid,
            StateGasTransitionKernel.RevertRefundToHalt(
                parentValue,
                parentStateReservoir,
                parentStateGasUsed,
                parentStateGasSpill,
                parentStateGasSpillRefunded,
                childStateGasUsed,
                childStateGasSpill,
                childStateGasSpillRefunded));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome RefundStateGas(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount,
        long stateGasFloor,
        bool trackSpillRefund) =>
        new(
            StateGasTransitionAdapterOutcomeKind.CompletedVoid,
            StateGasTransitionKernel.RefundStateGas(
                value,
                stateReservoir,
                stateGasUsed,
                stateGasSpill,
                stateGasSpillRefunded,
                amount,
                stateGasFloor,
                trackSpillRefund));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome DiscardStateGas(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount,
        long stateGasFloor) =>
        new(
            StateGasTransitionAdapterOutcomeKind.CompletedDiscard,
            StateGasTransitionKernel.DiscardStateGas(
                value,
                stateReservoir,
                stateGasUsed,
                stateGasSpill,
                stateGasSpillRefunded,
                amount,
                stateGasFloor));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome AddStateGasRefundToReservoir(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount,
        bool trackSpillRefund) =>
        new(
            StateGasTransitionAdapterOutcomeKind.CompletedVoid,
            StateGasTransitionKernel.AddStateGasRefundToReservoir(
                value,
                stateReservoir,
                stateGasUsed,
                stateGasSpill,
                stateGasSpillRefunded,
                amount,
                trackSpillRefund));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StateGasTransitionAdapterOutcome RemoveStateGasRefundFromReservoir(
        ulong value,
        long stateReservoir,
        long stateGasUsed,
        long stateGasSpill,
        long stateGasSpillRefunded,
        long amount) =>
        amount < 0
            ? new(
                StateGasTransitionAdapterOutcomeKind.ArgumentException,
                new StateGasTransitionResult(
                    value,
                    stateReservoir,
                    stateGasUsed,
                    stateGasSpill,
                    stateGasSpillRefunded,
                    0))
            : new(
                StateGasTransitionAdapterOutcomeKind.CompletedVoid,
                StateGasTransitionKernel.RemoveStateGasRefundFromReservoir(
                    value,
                    stateReservoir,
                    stateGasUsed,
                    stateGasSpill,
                    stateGasSpillRefunded,
                    amount));
}
