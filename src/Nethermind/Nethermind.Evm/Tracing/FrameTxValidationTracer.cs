// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Enforces the EIP-8141 validation-prefix trace and opcode rules while a frame transaction's
/// prefix is simulated at mempool admission, and captures the resolved payer.
/// </summary>
/// <remarks>
/// The gas bound (<c>MAX_VERIFY_GAS</c>) and the "halt once payer is set" rule are enforced by the
/// transaction processor's validation-prefix loop; this tracer enforces the per-opcode rules
/// (ethereum/EIPs#12007 "Validation Trace Rules"): the banned-opcode list, the <c>GAS</c>-before-call
/// caveat, the <c>TIMESTAMP</c>-in-expiry-verifier caveat, and <c>SLOAD</c> restricted to
/// <c>tx.sender</c> storage. A violation is recorded and the whole transaction is rejected; the
/// simulation runs against read-only state, so a banned write executed before detection is discarded.
/// EIP8141 follow-ups (design note §4 "Alternative C"): the first-<c>deploy</c>-frame carve-outs for
/// <c>CREATE</c>/<c>CREATE2</c>/<c>SETDELEGATE</c> and <c>SSTORE</c>-to-sender, and the
/// <c>CALL*</c>/<c>EXTCODE*</c> target-existence and EIP-7702 checks, are not yet enforced — prefixes
/// that need them are conservatively rejected here (declining is always spec-compliant, L684).
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
public sealed class FrameTxValidationTracer(Address sender, Address expiryVerifier) : TxTracer, IFrameTxReceiptTracer
{
    private const byte SetDelegateOpcode = 0xf6; // EIP-7819; not modelled as an Instruction on all forks.

    private bool _pendingGasRequiresCall;

    public override bool IsTracingInstructions => true;
    public override bool IsTracingOpLevelStorage => true;
    public override bool IsTracingReceipt => true;

    /// <summary>True once a trace/opcode rule was violated; the transaction must then be rejected.</summary>
    public bool Violated { get; private set; }

    /// <summary>Human-readable reason for the first recorded violation.</summary>
    public string? ViolationReason { get; private set; }

    /// <summary>The payer resolved by the simulated prefix, or <c>null</c> if it never set one.</summary>
    public Address? Payer { get; private set; }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (Violated) return;

        // GAS is permitted only when immediately followed by a *CALL (the standard gas-forwarding
        // idiom, which adds no public-mempool dependency); otherwise it is banned.
        if (_pendingGasRequiresCall)
        {
            _pendingGasRequiresCall = false;
            if (!IsCall(opcode))
            {
                Violate("GAS not immediately followed by a call");
                return;
            }
        }

        if ((byte)opcode == SetDelegateOpcode)
        {
            Violate("banned opcode SETDELEGATE in validation prefix");
            return;
        }

        switch (opcode)
        {
            case Instruction.GAS:
                _pendingGasRequiresCall = true;
                break;
            case Instruction.TIMESTAMP:
                // Permitted only inside the canonical expiry verifier frame (L779).
                if (env.ExecutingAccount != expiryVerifier) Violate("banned opcode TIMESTAMP in validation prefix");
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

    private static bool IsCall(Instruction opcode) => opcode is
        Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL;
}
