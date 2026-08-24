// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>Transaction-scoped context for an in-flight EIP-8141 frame transaction: the read-only envelope
/// plus the approval state the outer loop advances and the <c>APPROVE</c> opcode writes.</summary>
public sealed class FrameTxContext(
    Address sender,
    ulong nonce,
    TxFrame[] frames,
    TxFrameSignature[] signatures,
    ValueHash256 sigHash,
    in UInt256 maxCost,
    in UInt256 maxPriorityFeePerGas,
    in UInt256 maxFeePerGas,
    in UInt256 maxFeePerBlobGas,
    in UInt256 legacyNonce,
    RecentRootReference[]? recentRootReferences = null,
    UInt256[]? nonceKeys = null)
{
    public Address Sender { get; } = sender;
    public ulong Nonce { get; } = nonce;

    /// <summary>The EIP-8250 nonce keys this transaction consumes, or <see langword="null"/> for a plain account nonce.</summary>
    /// <remarks>When set, <see cref="Nonce"/> is the shared <c>nonce_seq</c> every key currently sits at.</remarks>
    public UInt256[]? NonceKeys { get; } = nonceKeys;

    /// <summary>The sender's account nonce before any frame executed.</summary>
    /// <remarks>Fixed for the whole transaction: approval, deployment and <c>CREATE</c> all move the live nonce.</remarks>
    public UInt256 LegacyNonce { get; } = legacyNonce;

    /// <summary><c>keccak256(bytes32(len(nonce_keys)) || concat(bytes32(k) for k in nonce_keys))</c>.</summary>
    /// <remarks>The EIP-8141 envelope hashes as the key set <c>[0]</c>, the domain its account nonce occupies.</remarks>
    public ValueHash256 NonceKeysHash =>
        NonceKeys is { } keys ? _nonceKeysHash ??= ComputeNonceKeysHash(keys) : AccountNonceKeySetHash;

    private ValueHash256? _nonceKeysHash;

    private static readonly ValueHash256 AccountNonceKeySetHash = ComputeNonceKeysHash([UInt256.Zero]);

    public TxFrame[] Frames { get; } = frames;
    public TxFrameSignature[] Signatures { get; } = signatures;
    public ValueHash256 SigHash { get; } = sigHash;
    public UInt256 MaxCost { get; } = maxCost;
    public UInt256 MaxPriorityFeePerGas { get; } = maxPriorityFeePerGas;
    public UInt256 MaxFeePerGas { get; } = maxFeePerGas;
    public UInt256 MaxFeePerBlobGas { get; } = maxFeePerBlobGas;

    /// <summary>The EIP-8272 recent-root references of the signed envelope, empty when it carries none.</summary>
    /// <remarks>Absent and empty are different envelopes but indistinguishable to executing code.</remarks>
    public RecentRootReference[] RecentRootReferences { get; } = recentRootReferences ?? [];

    /// <summary>Index of the frame currently executing; set by the outer loop before each frame.</summary>
    public int CurrentFrameIndex { get; set; }

    // MAX_FRAMES is 64, so one word holds every frame's bit.
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

    /// <summary>Scope deposited by a successful <c>APPROVE</c> in the current frame; 0 means no signal.
    /// The outer loop reads and clears it after the frame terminates.</summary>
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

    /// <summary>Journal position captured when an EVM call frame begins, so the rollback boundary that restores world state also restores the SSTORE-charge ownership map and per-frame <c>gas_used.state</c> corrections (EIP-8141 Gas Accounting).</summary>
    public int StateGasJournalCheckpoint => _stateGasJournal.Count;

    /// <summary>
    /// Records the frame that paid an <c>SSTORE</c> state charge as the outstanding-charge owner
    /// of <paramref name="slot"/>, so a later refill reduces that frame's receipt (spec: journal
    /// the charging frame's index as the outstanding charge owner).
    /// </summary>
    public void RecordStateChargeOwner(in StorageCell slot, int frame)
    {
        ref int owner = ref CollectionsMarshal.GetValueRefOrAddDefault(_stateChargeOwner, slot, out bool existed);
        int previousOwner = existed ? owner : NoOwner;
        owner = frame;
        _stateGasJournal.Add(new StateGasJournalEntry(StateGasJournalKind.OwnerSet, slot, previousOwner, 0));
    }

    /// <summary>
    /// Resolves and clears the outstanding-charge owner of <paramref name="slot"/> when a refill
    /// fires, journaling the cleared owner so a revert restores it (spec: clear the slot's ownership
    /// entry). Returns <c>false</c> when no frame owns an outstanding charge there.
    /// </summary>
    public bool TryResolveStateChargeOwner(in StorageCell slot, out int owner)
    {
        if (!_stateChargeOwner.Remove(slot, out owner))
        {
            return false;
        }

        _stateGasJournal.Add(new StateGasJournalEntry(StateGasJournalKind.OwnerCleared, slot, owner, 0));
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
        int count = _stateGasJournal.Count;
        if (count == checkpoint) return;

        Span<StateGasJournalEntry> entries = CollectionsMarshal.AsSpan(_stateGasJournal);
        for (int k = count - 1; k >= checkpoint; k--)
        {
            ref StateGasJournalEntry entry = ref entries[k];
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

        _stateGasJournal.RemoveRange(checkpoint, count - checkpoint);
    }

    /// <summary>The refill-driven reduction of <paramref name="frame"/>'s <c>gas_used.state</c>.</summary>
    public long StateGasCorrectionFor(int frame) => _frameStateGasCorrection[frame];

    private enum StateGasJournalKind : byte
    {
        OwnerSet,
        OwnerCleared,
        ReceiptReduced,
    }

    private readonly record struct StateGasJournalEntry(StateGasJournalKind Kind, StorageCell Slot, int Owner, long Amount);

    private static ValueHash256 ComputeNonceKeysHash(UInt256[] nonceKeys)
    {
        Span<byte> input = stackalloc byte[(1 + Eip8250Constants.MaxNonceKeys) * 32];
        new UInt256((ulong)nonceKeys.Length).ToBigEndian(input[..32]);
        for (int i = 0; i < nonceKeys.Length; i++)
        {
            nonceKeys[i].ToBigEndian(input.Slice((i + 1) * 32, 32));
        }

        return ValueKeccak.Compute(input[..((nonceKeys.Length + 1) * 32)]);
    }
}
