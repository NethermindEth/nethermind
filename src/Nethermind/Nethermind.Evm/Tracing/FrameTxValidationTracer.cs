// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
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
/// The gas bound (<c>MAX_VERIFY_GAS</c>) and the "halt once payer is set" rule are enforced by the
/// transaction processor's validation-prefix loop; this tracer enforces the per-opcode rules
/// (EIP-8141 "Validation Trace Rules"): the banned-opcode list, the <c>GAS</c>-before-call caveat,
/// the <c>TIMESTAMP</c>-in-expiry-verifier caveat, <c>SLOAD</c> restricted to <c>tx.sender</c>
/// storage, and the <c>CALL*</c>/<c>EXTCODE*</c> target rule — an existing contract or a precompile,
/// never an EIP-7702-delegated address, except for <c>tx.sender</c> whose code hash is a tracked
/// dependency. Simulation runs against read-only state, so a banned write reached before detection
/// is discarded.
/// The first-<c>deploy</c>-frame carve-outs for <c>CREATE</c>/<c>CREATE2</c>/<c>SETDELEGATE</c> and
/// <c>SSTORE</c>-to-sender are not honored, so the processor declines a prefix containing a deploy
/// frame before entering it — the unconditional bans below never fire for one (EIP8141-GAP).
/// </remarks>
/// <param name="token">Cancels the simulation cooperatively; polled by the interpreter.</param>
/// <param name="timeout">Wall-clock bound on the simulation, or <see cref="TimeSpan.Zero"/> for none.</param>
public sealed class FrameTxValidationTracer(
    Address sender,
    Address expiryVerifier,
    IReadOnlyStateProvider state,
    IReleaseSpec spec,
    CancellationToken token = default,
    TimeSpan timeout = default)
    : TxTracer, ITxTracer, IFrameTxReceiptTracer
{
    private const byte SetDelegateOpcode = 0xf6; // EIP-7819; not modelled as an Instruction on all forks.

    private readonly long _deadline = timeout > TimeSpan.Zero
        ? Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency)
        : 0;

    // Stack slot (from the top) holding the target address of the CALL*/EXTCODE* op currently being
    // traced, or -1 when the op is not one. Set in StartOperation, consumed in SetOperationStack for
    // the same instruction (the stack operands are unavailable to StartOperation).
    private int _targetStackIndex = -1;

    // Depth of the instruction being traced, recorded in StartOperation for the operand callbacks that
    // do not receive the execution environment.
    private int _callDepth;

    public override bool IsTracingInstructions => true;
    public override bool IsTracingOpLevelStorage => true;
    public override bool IsTracingStack => true;
    public override bool IsTracingReceipt => true;

    // Explicit: ITxTracer declares these as default members, so the mapping must be made on this type
    // for the interpreter (which polls through the interface) to see them.
    bool ITxTracer.IsCancelable => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Polled by the interpreter every 1024 opcodes. Aborting on a violation denies a spammer the rest of
    /// the <c>MAX_VERIFY_GAS</c> budget per rejected transaction, but only at call depth 0: unwinding out
    /// of a child frame would abandon its pooled <c>VmState</c>, and the caller checks
    /// <see cref="Violated"/> after execution anyway.
    /// </remarks>
    bool ITxTracer.IsCancelled => (Violated && _callDepth == 0) || TimedOut || token.IsCancellationRequested;

    /// <summary>True once the wall-clock bound was reached; the transaction is then rejected, not cancelled.</summary>
    public bool TimedOut => _deadline != 0 && Stopwatch.GetTimestamp() > _deadline;

    /// <summary>True once a trace/opcode rule was violated; the transaction must then be rejected.</summary>
    public bool Violated { get; private set; }

    /// <summary>Human-readable reason for the first recorded violation.</summary>
    public string? ViolationReason { get; private set; }

    /// <summary>The payer resolved by the simulated prefix, or <c>null</c> if it never set one.</summary>
    public Address? Payer { get; private set; }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        _targetStackIndex = -1;
        _callDepth = env.CallDepth;
        if (Violated) return;

        if ((byte)opcode == SetDelegateOpcode)
        {
            Violate("banned opcode SETDELEGATE in validation prefix");
            return;
        }

        switch (opcode)
        {
            case Instruction.GAS:
                // Permitted only immediately before a *CALL, the gas-forwarding idiom that adds no
                // mempool dependency. GAS has no immediate, so the next opcode is the byte at pc + 1.
                ReadOnlySpan<byte> code = env.CodeInfo.CodeSpan;
                int next = pc + 1;
                if (next >= code.Length || !IsCall((Instruction)code[next]))
                {
                    Violate("GAS not immediately followed by a call");
                }
                break;
            case Instruction.TIMESTAMP:
                // Permitted only while executing the canonical expiry-verifier runtime code, so both the
                // address and the code hash must match.
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
        // SLOAD may read only tx.sender storage, including transitively via CALL*/DELEGATECALL.
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
    /// or when it uses an EIP-7702 delegation. tx.sender is exempt: its code hash and nonce are already
    /// tracked dependencies, covering the default-code behavior the spec carves out.
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
