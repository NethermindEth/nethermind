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

/// <summary>Enforces the EIP-8141 validation-prefix opcode rules during mempool prefix simulation,
/// and captures the resolved payer.</summary>
/// <remarks>EIP8141-GAP: deploy-frame carve-outs unhandled, so the processor declines any prefix containing one.</remarks>
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
    private readonly long _deadline = timeout > TimeSpan.Zero
        ? Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency)
        : 0;

    // Stack slot holding the current CALL*/EXTCODE* target, or -1. Set in StartOperation and consumed in
    // SetOperationStack, as the operands aren't available any earlier.
    private int _targetStackIndex = -1;

    public override bool IsTracingInstructions => true;
    public override bool IsTracingOpLevelStorage => true;
    public override bool IsTracingStack => true;
    public override bool IsTracingReceipt => true;

    // Explicit: ITxTracer declares these as default members, so the mapping must be made on this type
    // for the interpreter (which polls through the interface) to see them.
    bool ITxTracer.IsCancelable => true;

    /// <inheritdoc/>
    /// <remarks>Polled by the interpreter every 1024 opcodes; aborting at the first violation denies a
    /// spammer the rest of the <c>MAX_VERIFY_GAS</c> budget per rejected transaction.</remarks>
    bool ITxTracer.IsCancelled => Violated || TimedOut || token.IsCancellationRequested;

    /// <summary>True once the wall-clock bound was reached; the transaction is then rejected, not cancelled.</summary>
    public bool TimedOut => _deadline != 0 && Stopwatch.GetTimestamp() > _deadline;

    public bool Violated { get; private set; }

    /// <summary>The first violation recorded; later ones do not overwrite it.</summary>
    public string? ViolationReason { get; private set; }

    public Address? Payer { get; private set; }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        _targetStackIndex = -1;
        if (Violated) return;

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
                // Permitted only inside the canonical expiry verifier: address and code hash must match.
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
            case Instruction.GASPRICE:
            case Instruction.BLOCKHASH:
            case Instruction.COINBASE:
            case Instruction.NUMBER:
            case Instruction.PREVRANDAO:
            case Instruction.GASLIMIT:
            case Instruction.BASEFEE:
            case Instruction.BLOBBASEFEE:
            case Instruction.SLOTNUM:
            case Instruction.CREATE:
            case Instruction.CREATE2:
            case Instruction.INVALID:
            case Instruction.SELFDESTRUCT:
            case Instruction.BALANCE:
            case Instruction.SELFBALANCE:
            case Instruction.SSTORE:
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

    /// <summary>Disallowed unless an undelegated existing contract or a precompile; tx.sender is exempt,
    /// its code hash and nonce being tracked dependencies already.</summary>
    private bool IsForbiddenCallTarget(Address target)
    {
        if (target == sender || spec.IsPrecompile(target)) return false;
        if (!state.IsContract(target)) return true;
        return state.IsDelegatedCode(target);
    }

    private static bool IsCall(Instruction opcode) => opcode is
        Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL;
}
