// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Transaction-scoped context for an in-flight EIP-8141 frame transaction. Holds the read-only
/// transaction shape (frames, signatures, sender, canonical hash, max cost) plus the mutable
/// approval state driven by the <c>APPROVE</c> opcode. One instance per frame transaction; the
/// outer frame-execution loop (the transaction processor) advances <see cref="CurrentFrameIndex"/>
/// and consumes <see cref="ApprovalScopeSignal"/> after each frame.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public sealed class FrameTxContext(
    Address sender,
    ulong nonce,
    TxFrame[] frames,
    TxFrameSignature[] signatures,
    ValueHash256 sigHash,
    in UInt256 maxCost,
    in UInt256 maxPriorityFeePerGas,
    in UInt256 maxFeePerGas,
    in UInt256 maxFeePerBlobGas)
{
    public Address Sender { get; } = sender;
    public ulong Nonce { get; } = nonce;
    public TxFrame[] Frames { get; } = frames;
    public TxFrameSignature[] Signatures { get; } = signatures;
    public ValueHash256 SigHash { get; } = sigHash;
    public UInt256 MaxCost { get; } = maxCost;
    public UInt256 MaxPriorityFeePerGas { get; } = maxPriorityFeePerGas;
    public UInt256 MaxFeePerGas { get; } = maxFeePerGas;
    public UInt256 MaxFeePerBlobGas { get; } = maxFeePerBlobGas;

    /// <summary>Index of the frame currently executing; set by the outer loop before each frame.</summary>
    public int CurrentFrameIndex { get; set; }

    /// <summary>Per-frame completion + success, populated as frames finish (for FRAMEPARAM status reads).</summary>
    public bool[] FrameCompleted { get; } = new bool[frames.Length];
    public bool[] FrameSucceeded { get; } = new bool[frames.Length];

    public bool SenderApproved { get; set; }
    public Address? Payer { get; set; }

    /// <summary>
    /// Scope deposited by a successful <c>APPROVE</c> in the current frame; 0 means no signal.
    /// The outer loop reads and clears it after the frame terminates.
    /// </summary>
    public byte ApprovalScopeSignal { get; set; }

    public TxFrame CurrentFrame => Frames[CurrentFrameIndex];

    public Address ResolvedTarget(int frameIndex) => Frames[frameIndex].Target ?? Sender;

    public Address ResolvedSigner(int signatureIndex) => Signatures[signatureIndex].Signer ?? Sender;
}
