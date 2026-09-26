// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>
    /// Applies the EIP-8360 balance-change gas tables to the value a frame moves on entry.
    /// </summary>
    /// <remarks>
    /// Runs in the entered frame, before its credit, so a revert or halt of the frame undoes the charges' state
    /// effects together with the transfer. The caller's debit has already been applied by the parent.
    /// </remarks>
    /// <returns><see langword="false"/> when the frame cannot pay the charges.</returns>
    private bool TryChargeTransientCreateTransfer(VmState<TGasPolicy> frame)
    {
        ExecutionEnvironment env = frame.Env;
        ref readonly UInt256 value = ref env.Value;
        if (value.IsZero || frame.ExecutionType is ExecutionType.TRANSACTION || !frame.ExecutionType.CreditsBalance())
            return true;

        Address from = env.Caller;
        Address to = env.ExecutingAccount;
        if (from.Equals(to))
            return true;

        ref readonly StackAccessTracker tracker = ref frame.AccessTracker;
        if (tracker.IsTransientCreate(from))
        {
            UInt256 fromBalance = _worldState.GetBalance(from);
            if (!TryChargeTransientCreateBalanceChange(frame, ref frame.Gas, from, fromBalance + value, in fromBalance))
                return false;
        }

        if (tracker.IsTransientCreate(to))
        {
            UInt256 toBalance = _worldState.GetBalance(to);
            if (!TryChargeTransientCreateBalanceChange(frame, ref frame.Gas, to, in toBalance, toBalance + value))
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
    /// away from the original balance and refunded when it is restored. Accounts that are not <c>TCREATE</c>
    /// accounts are ignored.
    /// </remarks>
    /// <returns><see langword="false"/> when <paramref name="gas"/> cannot pay the charge.</returns>
    internal bool TryChargeTransientCreateBalanceChange(VmState<TGasPolicy> frame, ref TGasPolicy gas, Address account, in UInt256 current, in UInt256 next)
    {
        if (current == next || !frame.AccessTracker.IsTransientCreate(account))
            return true;

        IReleaseSpec spec = Spec;
        UInt256 original = frame.AccessTracker.GetTransientCreateOriginalBalance(account);

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
