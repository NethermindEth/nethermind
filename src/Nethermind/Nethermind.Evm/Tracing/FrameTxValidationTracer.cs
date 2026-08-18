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
/// <remarks>Covers the per-opcode <see href="https://eips.ethereum.org/EIPS/eip-8141">validation trace rules</see>
/// only.</remarks>
public sealed class FrameTxValidationTracer(Address sender, Address expiryVerifier, IReadOnlyStateProvider state, IReleaseSpec spec)
    : TxTracer, IFrameTxReceiptTracer, IFrameTxPrefixTracer
{
    // Stack slot holding the current CALL*/EXTCODE* target, or -1. Set in StartOperation and consumed in
    // SetOperationStack for the same instruction, as the stack operands aren't available any earlier.
    private int _targetStackIndex = -1;

    /// <summary>Whether the executing frame is the prefix-opening deploy frame, the only one whose
    /// carve-outs let it write state.</summary>
    private bool _inDeployFrame;

    /// <summary>Set between a CREATE/CREATE2 and the creation frame it is expected to open.</summary>
    private bool _createPending;

    public override bool IsTracingInstructions => true;
    public override bool IsTracingOpLevelStorage => true;
    public override bool IsTracingStack => true;
    public override bool IsTracingReceipt => true;

    // The VM reports the address a creation frame is entered at; recomputing it here would duplicate
    // CREATE2's initcode hashing for no gain.
    public override bool IsTracingActions => true;

    public bool Violated { get; private set; }

    /// <summary>The first violation recorded; later ones do not overwrite it.</summary>
    public string? ViolationReason { get; private set; }

    public Address? Payer { get; private set; }

    void IFrameTxPrefixTracer.StartPrefixFrame(bool isDeployFrame)
    {
        SettleCreate();
        _inDeployFrame = isDeployFrame;
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        _targetStackIndex = -1;
        SettleCreate();
        if (Violated) return;

        switch (opcode)
        {
            case Instruction.GAS:
                // Permitted only in the gas-forwarding idiom, immediately followed by a *CALL (L836). GAS
                // takes no immediate operand, so the next opcode is the byte at pc + 1.
                ReadOnlySpan<byte> code = env.CodeInfo.CodeSpan;
                int next = pc + 1;
                if (next >= code.Length || !IsCall((Instruction)code[next]))
                {
                    Violate("GAS not immediately followed by a call");
                }
                break;
            case Instruction.TIMESTAMP:
                // Permitted only inside the canonical expiry verifier: address and code hash must match (L788).
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
            case Instruction.INVALID:
            case Instruction.SELFDESTRUCT:
            case Instruction.BALANCE:
            case Instruction.SELFBALANCE:
            case Instruction.TLOAD:
            case Instruction.TSTORE:
                Violate($"banned opcode {opcode} in validation prefix");
                break;
            case Instruction.CREATE:
            case Instruction.CREATE2:
                // Allowed only in the deploy frame, and only to install code at tx.sender; the address
                // is checked when the creation frame opens.
                if (!_inDeployFrame) Violate($"banned opcode {opcode} in validation prefix");
                else _createPending = true;
                break;
            case Instruction.SSTORE:
                // Allowed only in the deploy frame; SetOperationStorage confines it to tx.sender.
                if (!_inDeployFrame) Violate($"banned opcode {opcode} in validation prefix");
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

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        // Only reachable from the deploy frame, whose carve-out covers tx.sender's storage alone.
        if (!Violated && address != sender) Violate("SSTORE outside tx.sender storage");
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        if (!callType.IsAnyCreate()) return;

        _createPending = false;
        // The carve-out covers code installed at tx.sender only; a contract deployed anywhere else is a
        // state write whose result the prefix would silently depend on.
        if (!Violated && to != sender) Violate("CREATE outside tx.sender");
    }

    void IFrameTxReceiptTracer.ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) => Payer = payer;

    /// <summary>Closes out a CREATE/CREATE2 that opened no creation frame.</summary>
    /// <remarks>Such a create returned zero on a collision, balance or call-depth condition, none of
    /// which is an indexed dependency of the transaction, so the prefix must not turn on it.</remarks>
    private void SettleCreate()
    {
        if (!_createPending) return;
        _createPending = false;
        Violate("CREATE opened no creation frame");
    }

    private void Violate(string reason)
    {
        if (Violated) return;
        Violated = true;
        ViolationReason = reason;
    }

    /// <summary>
    /// A CALL*/EXTCODE* target is disallowed unless it is an undelegated existing contract (spec L816);
    /// tx.sender is exempt because its code hash and nonce are already tracked dependencies.
    /// </summary>
    private bool IsForbiddenCallTarget(Address target)
    {
        if (target == sender || spec.IsPrecompile(target)) return false;
        if (!state.IsContract(target)) return true;
        return state.IsDelegatedCode(target);
    }

    private static bool IsCall(Instruction opcode) => opcode is
        Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL;
}
