// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Enforces the EIP-8141 validation-prefix trace and opcode rules while a frame transaction's
/// prefix is simulated at mempool admission, and captures the resolved payer.
/// </summary>
/// <remarks>
/// The gas bound and the "halt once payer is set" rule live in the processor's prefix loop; this
/// enforces the per-opcode rules of EIP-8141 "Validation Trace Rules". <c>tx.sender</c> is exempt from
/// the <c>CALL*</c>/<c>EXTCODE*</c> target rule because its code hash is a tracked dependency.
/// The first-<c>deploy</c>-frame carve-outs are not honored, so the processor declines a prefix
/// containing a deploy frame before entering it and the bans below never fire for one (EIP8141-GAP).
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
public sealed class FrameTxValidationTracer(Address sender, Address expiryVerifier, IReadOnlyStateProvider state, IReleaseSpec spec)
    : TxTracer, IFrameTxReceiptTracer
{
    private const byte SetDelegateOpcode = 0xf6; // EIP-7819; not modelled as an Instruction on all forks.

    // Stack slot (from the top) holding the target address of the CALL*/EXTCODE* op currently being
    // traced, or -1 when the op is not one. Set in StartOperation, consumed in SetOperationStack for
    // the same instruction (the stack operands are unavailable to StartOperation).
    private int _targetStackIndex = -1;

    public override bool IsTracingInstructions => true;
    public override bool IsTracingOpLevelStorage => true;
    public override bool IsTracingStack => true;
    public override bool IsTracingReceipt => true;

    /// <summary>True once a trace/opcode rule was violated; the transaction must then be rejected.</summary>
    public bool Violated { get; private set; }

    /// <summary>Human-readable reason for the first recorded violation.</summary>
    public string? ViolationReason { get; private set; }

    /// <summary>The payer resolved by the simulated prefix, or <c>null</c> if it never set one.</summary>
    public Address? Payer { get; private set; }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        _targetStackIndex = -1;
        if (Violated) return;

        if ((byte)opcode == SetDelegateOpcode)
        {
            Violate("banned opcode SETDELEGATE in validation prefix");
            return;
        }

        switch (opcode)
        {
            case Instruction.GAS:
                // GAS is permitted only when immediately followed by a *CALL (the standard gas-forwarding
                // idiom, which adds no public-mempool dependency); otherwise it is banned (L836). GAS has
                // no immediate operand, so the next opcode is the byte at pc + 1; an implicit STOP at the
                // end of code is not a *CALL and is a violation.
                ReadOnlySpan<byte> code = env.CodeInfo.CodeSpan;
                int next = pc + 1;
                if (next >= code.Length || !IsCall((Instruction)code[next]))
                {
                    Violate("GAS not immediately followed by a call");
                }
                break;
            case Instruction.TIMESTAMP:
                // Permitted only inside a frame executing the canonical expiry verifier runtime code at
                // EXPIRY_VERIFIER (L788); both the address and the code hash must match.
                if (env.ExecutingAccount != expiryVerifier || state.GetCodeHash(expiryVerifier) != Eip8141Constants.ExpiryVerifierCodeHash)
                {
                    Violate("banned opcode TIMESTAMP in validation prefix");
                }
                break;
            case Instruction.CALL:
            case Instruction.CALLCODE:
            case Instruction.DELEGATECALL:
            case Instruction.STATICCALL:
                _targetStackIndex = 1; // [gas, address, ...]
                break;
            case Instruction.EXTCODESIZE:
            case Instruction.EXTCODEHASH:
            case Instruction.EXTCODECOPY:
                _targetStackIndex = 0; // [address, ...]
                break;
            case Instruction.ORIGIN:
            case Instruction.GASPRICE:
            case Instruction.BLOCKHASH:
            case Instruction.COINBASE:
            case Instruction.NUMBER:
            case Instruction.PREVRANDAO:
            case Instruction.GASLIMIT:
            case Instruction.BASEFEE:
            case Instruction.BLOBHASH:
            case Instruction.BLOBBASEFEE:
            case Instruction.CREATE:
            case Instruction.CREATE2:
            case Instruction.INVALID:
            case Instruction.SELFDESTRUCT:
            case Instruction.BALANCE:
            case Instruction.SELFBALANCE:
            case Instruction.SSTORE:
            case Instruction.TLOAD:
            case Instruction.TSTORE:
                Violate($"banned opcode {opcode} in validation prefix");
                break;
        }
    }

    public override void SetOperationStack(TraceStack stack)
    {
        if (Violated || _targetStackIndex < 0) return;

        int index = _targetStackIndex;
        _targetStackIndex = -1;
        // A malformed stack faults the frame and rejects the prefix anyway; skip classification.
        if (stack.Count <= index) return;

        Address target = stack.PeekAddress(index);
        if (IsForbiddenCallTarget(target))
        {
            Violate($"CALL*/EXTCODE* to disallowed target {target} in validation prefix");
        }
    }

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        // SLOAD may read only tx.sender storage, including transitively via CALL*/DELEGATECALL (L851).
        if (!Violated && address != sender) Violate("SLOAD outside tx.sender storage");
    }

    void IFrameTxReceiptTracer.ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) => Payer = payer;

    private void Violate(string reason)
    {
        if (Violated) return;
        Violated = true;
        ViolationReason = reason;
    }

    /// <summary>
    /// A CALL*/EXTCODE* target is disallowed when it is neither an existing contract nor a precompile,
    /// or when it uses an EIP-7702 delegation (spec L816). tx.sender is exempt: its code hash and nonce
    /// are already tracked dependencies, covering the default-code behavior the spec carves out.
    /// </summary>
    private bool IsForbiddenCallTarget(Address target)
    {
        if (target == sender || spec.IsPrecompile(target)) return false;
        if (!state.IsContract(target)) return true;      // codeless: not an existing contract
        return state.IsDelegatedCode(target);            // EIP-7702-delegated targets are forbidden
    }

    private static bool IsCall(Instruction opcode) => opcode is
        Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL;
}
