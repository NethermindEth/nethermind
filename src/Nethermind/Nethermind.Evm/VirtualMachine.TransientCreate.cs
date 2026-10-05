// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>
    /// Applies EIP-8360 on entry to a frame under a spec that enables it, before the frame's credit.
    /// </summary>
    /// <remarks>
    /// Records a <c>TCREATE</c> frame's account, sets <see cref="VmState{TGasPolicy}.IsTransientCreateContext"/>,
    /// and prices the frame's value transfer with the balance-change tables.
    /// </remarks>
    /// <returns><see langword="false"/> when the frame cannot pay the charges.</returns>
    private bool TryEnterTransientCreateFrame(VmState<TGasPolicy> frame)
    {
        ref readonly StackAccessTracker tracker = ref frame.AccessTracker;
        Address account = frame.Env.ExecutingAccount;
        if (frame.ExecutionType == ExecutionType.TCREATE)
        {
            // Recorded before the frame's initialization sets the account's nonce to 1, with nothing reading it in between.
            tracker.WasTransientlyCreated(account, _worldState.GetBalance(account));
            frame.IsTransientCreateContext = true;
            return TryChargeTransientCreateTransfer(frame, isNewTransientCreate: true);
        }

        if (tracker.TransientCreates is null)
        {
            frame.IsTransientCreateContext = false;
            return true;
        }

        frame.IsTransientCreateContext = tracker.IsTransientCreate(_worldState, account, out _);
        return TryChargeTransientCreateTransfer(frame, isNewTransientCreate: false);
    }

    /// <summary>
    /// Applies the EIP-8360 balance-change gas tables to the value a frame moves on entry.
    /// </summary>
    /// <remarks>
    /// Runs in the entered frame, before its credit, so a revert or halt of the frame undoes the charges' state
    /// effects together with the transfer. The caller's debit has already been applied by the parent or the processor.
    /// </remarks>
    /// <param name="frame">The frame being entered.</param>
    /// <param name="isNewTransientCreate">The frame is the <c>TCREATE</c> creating its account, whose nonce is not yet set.</param>
    /// <returns><see langword="false"/> when the frame cannot pay the charges.</returns>
    private bool TryChargeTransientCreateTransfer(VmState<TGasPolicy> frame, bool isNewTransientCreate)
    {
        ExecutionEnvironment env = frame.Env;
        ref readonly UInt256 value = ref env.Value;
        // TRANSACTION frames count too: an EIP-8141 frame can fund a TCREATE account created by an earlier frame.
        if (value.IsZero || !frame.ExecutionType.CreditsBalance())
            return true;

        Address from = env.Caller;
        Address to = env.ExecutingAccount;
        if (from.Equals(to))
            return true;

        ref readonly StackAccessTracker tracker = ref frame.AccessTracker;
        if (tracker.IsTransientCreate(_worldState, from, out UInt256 original))
        {
            UInt256 fromBalance = _worldState.GetBalance(from);
            if (!TryChargeTransientCreateBalanceChange(frame, ref frame.Gas, in original, fromBalance + value, in fromBalance))
                return false;
        }

        if (isNewTransientCreate || tracker.IsTransientCreate(_worldState, to, out original))
        {
            UInt256 toBalance = _worldState.GetBalance(to);
            if (isNewTransientCreate) original = toBalance;
            if (!TryChargeTransientCreateBalanceChange(frame, ref frame.Gas, in original, in toBalance, toBalance + value))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Charges or refunds gas for a balance change of an EIP-8360 <c>TCREATE</c> account.
    /// </summary>
    /// <remarks>
    /// State gas: <c>STATE_BYTES_PER_NEW_ACCOUNT × CPSB</c> is charged when a zero original balance first becomes
    /// non-zero and refilled when it returns to zero. Regular gas: <c>ACCOUNT_WRITE</c> is charged on the first move
    /// away from the original balance and refunded when it is restored.
    /// </remarks>
    /// <returns><see langword="false"/> when <paramref name="gas"/> cannot pay the charge.</returns>
    internal bool TryChargeTransientCreateBalanceChange(VmState<TGasPolicy> frame, ref TGasPolicy gas, in UInt256 original, in UInt256 current, in UInt256 next)
    {
        if (current == next)
            return true;

        IReleaseSpec spec = Spec;

        // Execution gas first so an execution-gas OOG does not spill state gas.
        if (spec.IsEip8038Enabled)
        {
            if (current == original)
            {
                if (!TGasPolicy.UpdateGas(ref gas, Eip8038Constants.AccountWrite))
                    return false;
            }
            else if (next == original)
            {
                frame.Refund += (long)Eip8038Constants.AccountWrite;
                if (IsTracingRefunds) _txTracer.ReportRefund((long)Eip8038Constants.AccountWrite);
            }
        }

        if (spec.IsEip8037Enabled && original.IsZero)
        {
            if (current.IsZero)
            {
                if (!TGasPolicy.TryConsumeStateGas(ref gas, TGasPolicy.GetNewAccountStateCost()))
                    return false;
            }
            else if (next.IsZero)
            {
                CreditStateGasRefund(ref gas, TGasPolicy.GetNewAccountStateCost());
            }
        }

        return true;
    }
}
