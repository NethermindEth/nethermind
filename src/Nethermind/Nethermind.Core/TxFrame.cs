// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// A single frame of an EIP-8141 frame transaction: <c>[mode, flags, target, limits, value, data]</c>,
/// where <c>limits = [execution, state]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
/// <param name="mode">One of the <c>Mode*</c> constants; it fixes both the caller the frame runs as and where
/// the frame may appear in the transaction.</param>
/// <param name="flags">The approval scope the frame may grant, plus <see cref="AtomicBatchFlag"/>.</param>
/// <param name="target">The frame's target, or <see langword="null"/> for the transaction sender.</param>
/// <param name="executionGasLimit">EIP-8141 <c>limits.execution</c>.</param>
/// <param name="stateGasLimit">EIP-8141 <c>limits.state</c>.</param>
/// <param name="value">Wei moved from the frame's caller to its target.</param>
/// <param name="data">The frame's calldata.</param>
public class TxFrame(byte mode, byte flags, Address? target, ulong executionGasLimit, ulong stateGasLimit, UInt256 value, ReadOnlyMemory<byte> data)
{
    /// <summary>An ordinary frame, called by the transaction sender.</summary>
    public const byte ModeDefault = 0;

    /// <summary>A validation-prefix frame: it may approve execution or payment, and runs before any body frame.</summary>
    public const byte ModeVerify = 1;

    /// <summary>A frame whose calls appear to come from the transaction sender rather than from the frame's caller.</summary>
    public const byte ModeSender = 2;

    /// <summary>EIP-7906: a read-only trailing frame that asserts the transaction's outcome.</summary>
    public const byte ModePostTx = 3;

    /// <summary>The frame may approve nothing; an <c>APPROVE</c> from it always reverts.</summary>
    public const byte ApproveScopeNone = 0x0;

    /// <summary>The frame may nominate its target as the transaction's payer.</summary>
    public const byte ApprovePayment = 0x1;

    /// <summary>The frame may authorize the transaction on the sender's behalf.</summary>
    public const byte ApproveExecution = 0x2;

    /// <summary>The frame may grant both scopes, together or one at a time.</summary>
    public const byte ApproveExecutionAndPayment = 0x3;

    /// <summary>The bits of <see cref="Flags"/> that carry the approval scope.</summary>
    public const byte ApproveScopeMask = ApproveExecutionAndPayment;

    /// <summary>Ties the frame to the batch it opens or continues: any member failing unrolls them all.</summary>
    public const byte AtomicBatchFlag = 0x4;

    /// <summary>Constructs a frame whose entire budget is execution gas, with <c>limits.state == 0</c>.</summary>
    public TxFrame(byte mode, byte flags, Address? target, ulong gasLimit, UInt256 value, ReadOnlyMemory<byte> data)
        : this(mode, flags, target, gasLimit, 0, value, data)
    {
    }

    /// <summary>The frame's <c>Mode*</c> kind.</summary>
    public byte Mode { get; } = mode;

    /// <summary>The frame's approval scope and batch membership; read through <see cref="AllowedApproveScope"/>
    /// and <see cref="IsAtomicBatch"/> rather than directly.</summary>
    public byte Flags { get; } = flags;

    /// <summary>Null resolves to the transaction sender during execution.</summary>
    public Address? Target { get; } = target;

    /// <summary>EIP-8141 <c>limits.execution</c>: the frame's execution-gas budget.</summary>
    public ulong ExecutionGasLimit { get; } = executionGasLimit;

    /// <summary>EIP-8141 <c>limits.state</c>: the frame's state-gas budget (EIP-8037).</summary>
    public ulong StateGasLimit { get; } = stateGasLimit;

    /// <summary>The combined gas the frame reserves against the payer, <c>limits.execution + limits.state</c>.</summary>
    public ulong GasLimit => ExecutionGasLimit + StateGasLimit;

    /// <summary>Wei the frame moves from its caller to <see cref="Target"/>.</summary>
    public UInt256 Value { get; } = value;

    /// <summary>The frame's calldata.</summary>
    public ReadOnlyMemory<byte> Data { get; } = data;

    /// <summary>The widest scope an <c>APPROVE</c> from this frame may request.</summary>
    public byte AllowedApproveScope => (byte)(Flags & ApproveScopeMask);

    /// <summary>Whether the frame belongs to an atomic batch, whose members stand or fall together.</summary>
    public bool IsAtomicBatch => (Flags & AtomicBatchFlag) != 0;
}
