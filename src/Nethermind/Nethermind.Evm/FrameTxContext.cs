// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>Transaction-scoped context for an in-flight EIP-8141 frame transaction: the read-only envelope
/// plus the approval state the outer loop advances and the <c>APPROVE</c> opcode writes.</summary>
/// <remarks>One instance per transaction, not thread-safe: it is mutated by the single frame loop executing it.</remarks>
/// <param name="sender">The transaction sender, and the default target and signer of frames and entries that name none.</param>
/// <param name="nonce">The nonce the envelope was signed at: the account nonce, or the shared <c>nonce_seq</c> when
/// <paramref name="nonceKeys"/> is set.</param>
/// <param name="frames">The envelope's frames, in execution order.</param>
/// <param name="signatures">The envelope's signature entries, already validated when execution begins.</param>
/// <param name="sigHash">The canonical signature hash the entries that carry no explicit <c>msg</c> sign.</param>
/// <param name="maxCost">The gas and blob-gas cost the payer's approval reserves up front.</param>
/// <param name="maxPriorityFeePerGas">EIP-1559 <c>max_priority_fee_per_gas</c> of the envelope.</param>
/// <param name="maxFeePerGas">EIP-1559 <c>max_fee_per_gas</c> of the envelope.</param>
/// <param name="maxFeePerBlobGas">EIP-4844 <c>max_fee_per_blob_gas</c> of the envelope, zero when it carries no blobs.</param>
/// <param name="legacyNonce">The sender's account nonce before any frame executed.</param>
/// <param name="recentRootReferences">EIP-8272 recent-root references of the signed envelope.</param>
/// <param name="nonceKeys">EIP-8250 nonce keys, or <see langword="null"/> for a plain account nonce.</param>
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
    /// <summary>The transaction sender: the default target of a frame and signer of an entry that names none.</summary>
    public Address Sender { get; } = sender;

    /// <summary>The nonce the envelope was signed at; with <see cref="NonceKeys"/> set, the shared <c>nonce_seq</c>.</summary>
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

    /// <summary>The envelope's frames, in execution order.</summary>
    public TxFrame[] Frames { get; } = frames;

    /// <summary>The envelope's signature entries, validated before the first frame runs.</summary>
    public TxFrameSignature[] Signatures { get; } = signatures;

    /// <summary>The hash entries carrying no explicit <c>msg</c> are taken to have signed.</summary>
    public ValueHash256 SigHash { get; } = sigHash;

    /// <summary>The cost an approving payer reserves up front: the whole gas budget plus blob gas at the
    /// envelope's maximum prices.</summary>
    public UInt256 MaxCost { get; } = maxCost;

    /// <summary>EIP-1559 <c>max_priority_fee_per_gas</c> of the envelope.</summary>
    public UInt256 MaxPriorityFeePerGas { get; } = maxPriorityFeePerGas;

    /// <summary>EIP-1559 <c>max_fee_per_gas</c> of the envelope.</summary>
    public UInt256 MaxFeePerGas { get; } = maxFeePerGas;

    /// <summary>EIP-4844 <c>max_fee_per_blob_gas</c> of the envelope; zero when it carries no blobs.</summary>
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

    /// <summary>Whether a completed frame ran to success; meaningless for a frame that has not completed.</summary>
    public bool HasFrameSucceeded(int frameIndex) => (_frameSucceededBits & (1UL << frameIndex)) != 0;

    /// <summary>Records that <paramref name="frameIndex"/> completed successfully.</summary>
    public void MarkFrameSucceeded(int frameIndex) => _frameSucceededBits |= 1UL << frameIndex;

    /// <summary>Whether a frame was skipped rather than run, as a failed atomic batch's remaining members are.</summary>
    public bool WasFrameSkipped(int frameIndex) => (_frameSkippedBits & (1UL << frameIndex)) != 0;

    /// <summary>Records that <paramref name="frameIndex"/> was skipped rather than run.</summary>
    public void MarkFrameSkipped(int frameIndex) => _frameSkippedBits |= 1UL << frameIndex;


    /// <summary>Whether some frame has authorized the transaction on the sender's behalf.</summary>
    /// <remarks>Set through <see cref="ApplyApproval"/>, which journals it; assigning it directly skips the undo record.</remarks>
    public bool SenderApproved { get; set; }

    /// <summary>The account whose balance covers the transaction, once a frame has approved payment.</summary>
    /// <remarks>Write-once for the life of the transaction, apart from a journal restore.</remarks>
    public Address? Payer { get; set; }

    /// <summary>The frame the outer loop is currently executing.</summary>
    public TxFrame CurrentFrame => Frames[CurrentFrameIndex];

    /// <summary>EIP-7906: lazily-built, sorted view of this transaction's state diff and logs, shared by its POST_TX frames.</summary>
    internal TransactionDiffView? PostTxDiffView { get; set; }

    /// <summary>A frame's target with the omitted-target encoding resolved to the sender.</summary>
    public Address ResolvedTarget(int frameIndex) => Frames[frameIndex].Target ?? Sender;

    /// <summary>An entry's signer with the omitted-signer encoding resolved to the sender.</summary>
    public Address ResolvedSigner(int signatureIndex) => Signatures[signatureIndex].Signer ?? Sender;

    private const int NoOwner = -1;

    private readonly Dictionary<StorageCell, int> _stateChargeOwner = [];
    private readonly long[] _frameStateGasCorrection = new long[frames.Length];
    private readonly ulong[] _frameExecutionGasUsed = new ulong[frames.Length];
    private readonly ulong[] _frameStateGasUsed = new ulong[frames.Length];
    private readonly List<FrameJournalEntry> _frameJournal = [];

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

    /// <summary>Journal position captured when an EVM call frame begins, so the rollback boundary that restores world state also restores the approval context, the SSTORE-charge ownership map and per-frame <c>gas_used.state</c> corrections (EIP-8141 Gas Accounting).</summary>
    public int FrameJournalCheckpoint => _frameJournal.Count;

    // Approval only advances, none -> sender approved -> paid, and Payer is write-once, so the stage
    // number the journal keeps is a complete undo record.
    private const int NoApproval = 0;
    private const int SenderApprovedStage = 1;
    private const int PaidStage = 2;

    private int ApprovalStage => Payer is not null ? PaidStage : SenderApproved ? SenderApprovedStage : NoApproval;

    /// <summary>
    /// Evaluates an <c>APPROVE</c> of <paramref name="scope"/> by <paramref name="resolvedTarget"/> against
    /// the transaction's approval context, changing nothing.
    /// </summary>
    /// <remarks>Split from <see cref="ApplyApproval"/> so a caller can charge the approval's gas in between:
    /// a charge that cannot be met must not leave a half-applied approval behind.</remarks>
    /// <param name="plan">The effects an admitted approval will apply; meaningless unless the outcome is
    /// <see cref="FrameApprovalOutcome.Approved"/>.</param>
    internal FrameApprovalOutcome PlanApproval(byte scope, Address resolvedTarget, IWorldState worldState, out FrameApprovalPlan plan)
    {
        plan = default;
        if (scope == 0 || (scope & ~CurrentFrame.AllowedApproveScope) != 0) return FrameApprovalOutcome.Rejected;

        bool approvesExecution = (scope & TxFrame.ApproveExecution) != 0;
        bool approvesPayment = (scope & TxFrame.ApprovePayment) != 0;

        if (approvesExecution && (SenderApproved || resolvedTarget != Sender)) return FrameApprovalOutcome.Rejected;

        bool createsSender = false;
        if (approvesPayment)
        {
            if (Payer is not null) return FrameApprovalOutcome.Rejected;
            // EIP-8141 ordering: payment may not be approved before execution, unless this same APPROVE grants both.
            if (!approvesExecution && !SenderApproved) return FrameApprovalOutcome.Rejected;
            if (worldState.GetBalance(resolvedTarget) < MaxCost) return FrameApprovalOutcome.Rejected;

            if (NonceKeys is not { } keys || !KeyedNonceManager.UsesKeyedDomain(keys))
            {
                if (worldState.GetNonce(Sender) >= Eip8250Constants.MaxNonceSeq) return FrameApprovalOutcome.NonceExhausted;
                createsSender = !worldState.AccountExists(Sender);
            }
        }

        plan = new FrameApprovalPlan(approvesExecution, approvesPayment, createsSender);
        return FrameApprovalOutcome.Approved;
    }

    /// <summary>
    /// Applies an approval admitted by <see cref="PlanApproval"/>, journaled so the boundary that restores
    /// world state on a nested revert or halt restores the approval context with it.
    /// </summary>
    /// <remarks>The sender's account creation, when the plan calls for one, must already have been charged.</remarks>
    internal void ApplyApproval(in FrameApprovalPlan plan, Address resolvedTarget, IWorldState worldState, IReleaseSpec spec, in StackAccessTracker accessTracker)
    {
        _frameJournal.Add(new FrameJournalEntry(FrameJournalKind.ApprovalAdvanced, default, ApprovalStage, 0));

        if (plan.ApprovesExecution) SenderApproved = true;
        if (!plan.ApprovesPayment) return;

        if (plan.CreatesSender) worldState.CreateAccountIfNotExists(Sender, UInt256.Zero);
        worldState.SubtractFromBalance(resolvedTarget, MaxCost, spec);
        if (NonceKeys is { } nonceKeys)
        {
            KeyedNonceManager.ConsumeNonceSet(worldState, Sender, nonceKeys, Nonce);
        }
        else
        {
            worldState.IncrementNonce(Sender);
        }

        Payer = resolvedTarget;
        if (spec.UseHotAndColdStorage) accessTracker.WarmUp(resolvedTarget);
    }

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
        _frameJournal.Add(new FrameJournalEntry(FrameJournalKind.OwnerSet, slot, previousOwner, 0));
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

        _frameJournal.Add(new FrameJournalEntry(FrameJournalKind.OwnerCleared, slot, owner, 0));
        return true;
    }

    /// <summary>
    /// Subtracts a refilled state-gas charge from the receipt of the frame that paid it
    /// (spec: <c>frame_receipts[owner].gas_used.state -= amount</c>), journaled so a revert undoes it.
    /// </summary>
    public void ReduceFrameStateGas(int owner, long amount)
    {
        _frameStateGasCorrection[owner] += amount;
        _frameJournal.Add(new FrameJournalEntry(FrameJournalKind.ReceiptReduced, default, owner, amount));
    }

    /// <summary>
    /// Undoes approval, ownership and receipt-correction journal entries recorded after
    /// <paramref name="checkpoint"/>, at the same boundary that restores world state.
    /// </summary>
    public void RestoreFrameJournal(int checkpoint)
    {
        int count = _frameJournal.Count;
        if (count == checkpoint) return;

        Span<FrameJournalEntry> entries = CollectionsMarshal.AsSpan(_frameJournal);
        for (int k = count - 1; k >= checkpoint; k--)
        {
            ref FrameJournalEntry entry = ref entries[k];
            switch (entry.Kind)
            {
                case FrameJournalKind.OwnerSet:
                    if (entry.Value == NoOwner)
                    {
                        _stateChargeOwner.Remove(entry.Slot);
                    }
                    else
                    {
                        _stateChargeOwner[entry.Slot] = entry.Value;
                    }
                    break;
                case FrameJournalKind.OwnerCleared:
                    _stateChargeOwner[entry.Slot] = entry.Value;
                    break;
                case FrameJournalKind.ReceiptReduced:
                    _frameStateGasCorrection[entry.Value] -= entry.Amount;
                    break;
                case FrameJournalKind.ApprovalAdvanced:
                    SenderApproved = entry.Value >= SenderApprovedStage;
                    if (entry.Value < PaidStage) Payer = null;
                    break;
            }
        }

        _frameJournal.RemoveRange(checkpoint, count - checkpoint);
    }

    /// <summary>The refill-driven reduction of <paramref name="frame"/>'s <c>gas_used.state</c>.</summary>
    public long StateGasCorrectionFor(int frame) => _frameStateGasCorrection[frame];

    private enum FrameJournalKind : byte
    {
        OwnerSet,
        OwnerCleared,
        ReceiptReduced,
        ApprovalAdvanced,
    }

    /// <summary><see cref="Value"/> carries the entry's undo target: a frame index for the ownership and
    /// receipt kinds, the previous approval stage for <see cref="FrameJournalKind.ApprovalAdvanced"/>.</summary>
    private readonly record struct FrameJournalEntry(FrameJournalKind Kind, StorageCell Slot, int Value, long Amount);

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

/// <summary>Outcome of evaluating an EIP-8141 <c>APPROVE</c> against a transaction's approval context.</summary>
internal enum FrameApprovalOutcome : byte
{
    /// <summary>The approval is admissible.</summary>
    Approved,

    /// <summary>A guard refused the approval; the requesting call frame reverts.</summary>
    Rejected,

    /// <summary>The sender's nonce sequence is exhausted; the requesting call frame halts exceptionally.</summary>
    NonceExhausted,
}

/// <summary>The effects an admitted <c>APPROVE</c> will apply, so its caller can charge for them beforehand.</summary>
internal readonly record struct FrameApprovalPlan(bool ApprovesExecution, bool ApprovesPayment, bool CreatesSender);
