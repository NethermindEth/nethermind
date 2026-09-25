// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>The kind of an EIP-8141 frame: it fixes both the caller the frame runs as and where the frame may
/// appear in the transaction.</summary>
/// <remarks>Not closed at the wire boundary: a decoded frame can carry any byte, so
/// <see cref="FrameTxValidation.IsWellFormed"/> still range-checks the value.</remarks>
public enum FrameMode : byte
{
    /// <summary>An ordinary frame, called by <c>ENTRY_POINT</c>.</summary>
    Default = 0,

    /// <summary>A read-only frame, called by <c>ENTRY_POINT</c>, whose failure invalidates the whole transaction
    /// instead of merely reverting.</summary>
    /// <remarks>
    /// Ordinarily the transaction's validation prefix, where <see cref="TxFrame.Flags"/> lets it approve execution or
    /// payment; the approval scope comes from the flags, not from this mode. Consensus does not confine a VERIFY
    /// frame to that prefix — one may sit behind a body frame — though it may neither follow a POST_TX frame nor
    /// directly follow an atomic-batch one. The public mempool additionally refuses a VERIFY frame behind the
    /// first frame flagged to approve payment (<see cref="FrameTxValidation.HasVerifyFrameAfterPrefix"/>): its
    /// revert would invalidate an already-announced transaction on state the pool never simulated.
    /// </remarks>
    Verify = 1,

    /// <summary>The one mode called by the transaction sender rather than <c>ENTRY_POINT</c>, and so the only one
    /// that may carry <see cref="TxFrame.Value"/>.</summary>
    /// <remarks>Rejected at execution unless an earlier frame has already approved execution for the sender.</remarks>
    Sender = 2,

    /// <summary>EIP-7906: a read-only trailing frame, called by <c>ENTRY_POINT</c>, that asserts the
    /// transaction's outcome.</summary>
    PostTx = 3,
}

/// <summary>The approval scope an EIP-8141 frame may grant, plus its atomic-batch membership.</summary>
/// <remarks>Not closed at the wire boundary: a decoded frame can carry any bit pattern, so
/// <see cref="FrameTxValidation.IsWellFormed"/> still rejects the reserved bits.</remarks>
[Flags]
public enum FrameFlags : byte
{
    /// <summary>The frame may approve nothing; an <c>APPROVE</c> from it always reverts.</summary>
    None = 0x0,

    /// <summary>The frame may nominate its target as the transaction's payer.</summary>
    ApprovePayment = 0x1,

    /// <summary>The frame may authorize the transaction on the sender's behalf.</summary>
    ApproveExecution = 0x2,

    /// <summary>The frame may grant both scopes, together or one at a time.</summary>
    ApproveExecutionAndPayment = 0x3,

    /// <summary>Ties the frame to the batch it opens or continues: any member failing unrolls them all.</summary>
    AtomicBatch = 0x4,
}

/// <summary>
/// A single frame of an EIP-8141 frame transaction: <c>[mode, flags, target, limits, value, data]</c>,
/// where <c>limits = [execution, state]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
/// <param name="mode">The frame's kind; it fixes both the caller the frame runs as and where
/// the frame may appear in the transaction.</param>
/// <param name="flags">The approval scope the frame may grant, plus <see cref="FrameFlags.AtomicBatch"/>.</param>
/// <param name="target">The frame's target, or <see langword="null"/> for the transaction sender.</param>
/// <param name="executionGasLimit">EIP-8141 <c>limits.execution</c>.</param>
/// <param name="stateGasLimit">EIP-8141 <c>limits.state</c>.</param>
/// <param name="value">Wei moved from the frame's caller to its target.</param>
/// <param name="data">The frame's calldata.</param>
public class TxFrame(FrameMode mode, FrameFlags flags, Address? target, ulong executionGasLimit, ulong stateGasLimit, UInt256 value, ReadOnlyMemory<byte> data)
{
    /// <summary>The bits of <see cref="Flags"/> that carry the approval scope.</summary>
    public const FrameFlags ApproveScopeMask = FrameFlags.ApproveExecutionAndPayment;

    /// <summary>Constructs a frame whose entire budget is execution gas, with <c>limits.state == 0</c>.</summary>
    public TxFrame(FrameMode mode, FrameFlags flags, Address? target, ulong gasLimit, UInt256 value, ReadOnlyMemory<byte> data)
        : this(mode, flags, target, gasLimit, 0, value, data)
    {
    }

    /// <summary>The frame's kind.</summary>
    public FrameMode Mode { get; } = mode;

    /// <summary>The frame's approval scope and batch membership; read through <see cref="AllowedApproveScope"/>
    /// and <see cref="IsAtomicBatch"/> rather than directly.</summary>
    public FrameFlags Flags { get; } = flags;

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
    public FrameFlags AllowedApproveScope => Flags & ApproveScopeMask;

    /// <summary>Whether the frame belongs to an atomic batch, whose members stand or fall together.</summary>
    public bool IsAtomicBatch => (Flags & FrameFlags.AtomicBatch) != 0;
}
