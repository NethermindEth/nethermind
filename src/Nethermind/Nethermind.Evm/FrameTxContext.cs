// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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

    /// <summary>Per-frame success bits (MAX_FRAMES is 64), populated as frames finish.</summary>
    private ulong _frameSucceededBits;
    private ulong _frameSkippedBits;

    /// <summary>EVM code only runs while some frame executes, so completed means strictly earlier.</summary>
    public bool IsFrameCompleted(int frameIndex) => frameIndex < CurrentFrameIndex;

    public bool HasFrameSucceeded(int frameIndex) => (_frameSucceededBits & (1UL << frameIndex)) != 0;

    public void MarkFrameSucceeded(int frameIndex) => _frameSucceededBits |= 1UL << frameIndex;

    public bool WasFrameSkipped(int frameIndex) => (_frameSkippedBits & (1UL << frameIndex)) != 0;

    public void MarkFrameSkipped(int frameIndex) => _frameSkippedBits |= 1UL << frameIndex;


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

    private const int NoOwner = -1;

    private readonly Dictionary<StorageCell, int> _stateChargeOwner = [];
    private readonly long[] _frameStateGasCorrection = new long[frames.Length];
    private readonly ulong[] _frameExecutionGasUsed = new ulong[frames.Length];
    private readonly ulong[] _frameStateGasUsed = new ulong[frames.Length];
    private readonly List<StateGasJournalEntry> _stateGasJournal = [];

    /// <summary>
    /// Records a completed frame's attributed <c>gas_used</c> so a later frame can read it through
    /// <c>FRAMEPARAM</c> (spec: <c>frame_receipts[frame_index].gas_used</c>). The state component is
    /// the charge before any later refill; <see cref="StateGasUsedFor"/> nets off refill corrections.
    /// </summary>
    public void RecordFrameReceipt(int frame, ulong executionGasUsed, ulong stateGasUsed)
    {
        _frameExecutionGasUsed[frame] = executionGasUsed;
        _frameStateGasUsed[frame] = stateGasUsed;
    }

    /// <summary>Drops a completed frame's attributed state gas when an atomic-batch unroll clears its receipt.</summary>
    public void ClearFrameStateGasUsed(int frame) => _frameStateGasUsed[frame] = 0;

    /// <summary>A completed frame's attributed <c>gas_used.execution</c> (execution gas is never refilled).</summary>
    public ulong ExecutionGasUsedFor(int frame) => _frameExecutionGasUsed[frame];

    /// <summary>A completed frame's attributed <c>gas_used.state</c>, net of refills a later frame applied to it.</summary>
    public ulong StateGasUsedFor(int frame)
    {
        long net = (long)_frameStateGasUsed[frame] - _frameStateGasCorrection[frame];
        return net > 0 ? (ulong)net : 0;
    }

    /// <summary>
    /// Journal position covering the outstanding SSTORE-charge ownership map and the per-frame
    /// <c>gas_used.state</c> corrections, captured when an EVM call frame begins so the same
    /// rollback boundary that restores world state can restore these (EIP-8141 Gas Accounting).
    /// </summary>
    public int StateGasJournalCheckpoint => _stateGasJournal.Count;

    /// <summary>
    /// Records the frame that paid an <c>SSTORE</c> state charge as the outstanding-charge owner
    /// of <paramref name="slot"/>, so a later refill reduces that frame's receipt (spec: journal
    /// the charging frame's index as the outstanding charge owner).
    /// </summary>
    public void RecordStateChargeOwner(in StorageCell slot, int frame)
    {
        int previousOwner = _stateChargeOwner.TryGetValue(slot, out int existing) ? existing : NoOwner;
        _stateGasJournal.Add(new StateGasJournalEntry(StateGasJournalKind.OwnerSet, slot, previousOwner, 0));
        _stateChargeOwner[slot] = frame;
    }

    /// <summary>
    /// Resolves and clears the outstanding-charge owner of <paramref name="slot"/> when a refill
    /// fires, journaling the cleared owner so a revert restores it (spec: clear the slot's ownership
    /// entry). Returns <c>false</c> when no frame owns an outstanding charge there.
    /// </summary>
    public bool TryResolveStateChargeOwner(in StorageCell slot, out int owner)
    {
        if (!_stateChargeOwner.TryGetValue(slot, out owner))
        {
            return false;
        }

        _stateGasJournal.Add(new StateGasJournalEntry(StateGasJournalKind.OwnerCleared, slot, owner, 0));
        _stateChargeOwner.Remove(slot);
        return true;
    }

    /// <summary>
    /// Subtracts a refilled state-gas charge from the receipt of the frame that paid it
    /// (spec: <c>frame_receipts[owner].gas_used.state -= amount</c>), journaled so a revert undoes it.
    /// </summary>
    public void ReduceFrameStateGas(int owner, long amount)
    {
        _frameStateGasCorrection[owner] += amount;
        _stateGasJournal.Add(new StateGasJournalEntry(StateGasJournalKind.ReceiptReduced, default, owner, amount));
    }

    /// <summary>
    /// Undoes ownership and receipt-correction journal entries recorded after
    /// <paramref name="checkpoint"/>, at the same boundary that restores world state.
    /// </summary>
    public void RestoreStateGasJournal(int checkpoint)
    {
        for (int k = _stateGasJournal.Count - 1; k >= checkpoint; k--)
        {
            StateGasJournalEntry entry = _stateGasJournal[k];
            switch (entry.Kind)
            {
                case StateGasJournalKind.OwnerSet:
                    if (entry.Owner == NoOwner)
                    {
                        _stateChargeOwner.Remove(entry.Slot);
                    }
                    else
                    {
                        _stateChargeOwner[entry.Slot] = entry.Owner;
                    }
                    break;
                case StateGasJournalKind.OwnerCleared:
                    _stateChargeOwner[entry.Slot] = entry.Owner;
                    break;
                case StateGasJournalKind.ReceiptReduced:
                    _frameStateGasCorrection[entry.Owner] -= entry.Amount;
                    break;
            }
        }

        _stateGasJournal.RemoveRange(checkpoint, _stateGasJournal.Count - checkpoint);
    }

    /// <summary>The refill-driven reduction of <paramref name="frame"/>'s <c>gas_used.state</c>.</summary>
    public long StateGasCorrectionFor(int frame) => _frameStateGasCorrection[frame];

    /// <summary>The total refill-driven reduction across all frames, subtracted from the tx dimensions at settlement.</summary>
    public long TotalStateGasCorrection
    {
        get
        {
            long total = 0;
            foreach (long correction in _frameStateGasCorrection)
            {
                total += correction;
            }
            return total;
        }
    }

    private enum StateGasJournalKind : byte
    {
        OwnerSet,
        OwnerCleared,
        ReceiptReduced,
    }

    private readonly record struct StateGasJournalEntry(StateGasJournalKind Kind, StorageCell Slot, int Owner, long Amount);
}
