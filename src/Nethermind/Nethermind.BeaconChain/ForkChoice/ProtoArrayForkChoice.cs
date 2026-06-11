// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// LMD-GHOST fork choice tying together the proto-array block tree, the per-validator latest
/// messages, and the justified balances. Ported from Lighthouse's <c>ProtoArrayForkChoice</c>.
/// </summary>
public sealed class ProtoArrayForkChoice
{
    public const int DefaultPruneThreshold = 256;

    /// <summary>Mainnet <c>SLOTS_PER_EPOCH</c>.</summary>
    public const ulong DefaultSlotsPerEpoch = 32;

    /// <summary>Mainnet <c>PROPOSER_SCORE_BOOST</c>, in percent of a single committee's weight.</summary>
    public const ulong DefaultProposerScoreBoostPercent = 40;

    private readonly ProtoArray _protoArray;
    private readonly VoteTrackerList _votes = new();
    private readonly HashSet<ulong> _equivocatingIndices = [];
    private JustifiedBalances _balances = JustifiedBalances.Empty;

    /// <summary>Creates a fork choice rooted at the given finalized (anchor) block.</summary>
    /// <param name="currentSlot">The wall-clock slot at creation time.</param>
    /// <param name="finalizedBlockSlot">The slot of the anchor block.</param>
    /// <param name="finalizedBlockStateRoot">The state root of the anchor block.</param>
    /// <param name="justifiedCheckpoint">The justified checkpoint of the anchor state.</param>
    /// <param name="finalizedCheckpoint">The finalized checkpoint; its root is the anchor block root.</param>
    /// <param name="executionStatus">The execution status of the anchor block.</param>
    /// <param name="executionBlockHash">The anchor's execution payload hash; <c>null</c> if and only if <paramref name="executionStatus"/> is <see cref="ExecutionStatus.Irrelevant"/>.</param>
    /// <param name="slotsPerEpoch">Number of slots per epoch.</param>
    /// <param name="proposerScoreBoostPercent">The proposer boost as a percentage of a single committee's weight.</param>
    public ProtoArrayForkChoice(
        ulong currentSlot,
        ulong finalizedBlockSlot,
        Hash256 finalizedBlockStateRoot,
        CheckpointRef justifiedCheckpoint,
        CheckpointRef finalizedCheckpoint,
        ExecutionStatus executionStatus,
        Hash256? executionBlockHash,
        ulong slotsPerEpoch = DefaultSlotsPerEpoch,
        ulong proposerScoreBoostPercent = DefaultProposerScoreBoostPercent)
    {
        _protoArray = new ProtoArray(slotsPerEpoch, proposerScoreBoostPercent);

        ProtoBlock anchor = new(
            Slot: finalizedBlockSlot,
            Root: finalizedCheckpoint.Root,
            ParentRoot: null,
            StateRoot: finalizedBlockStateRoot,
            // The finalized block root always lies on an epoch boundary, so it is its own target.
            TargetRoot: finalizedCheckpoint.Root,
            JustifiedCheckpoint: justifiedCheckpoint,
            FinalizedCheckpoint: finalizedCheckpoint,
            ExecutionStatus: executionStatus,
            ExecutionBlockHash: executionBlockHash,
            UnrealizedJustifiedCheckpoint: justifiedCheckpoint,
            UnrealizedFinalizedCheckpoint: finalizedCheckpoint);

        _protoArray.OnBlock(anchor, currentSlot, justifiedCheckpoint, finalizedCheckpoint);
    }

    /// <summary>The root receiving the proposer score boost in the next <see cref="GetHead"/> call; <see cref="Hash256.Zero"/> when no boost applies.</summary>
    public Hash256 ProposerBoostRoot { get; private set; } = Hash256.Zero;

    /// <summary>The number of nodes in the proto-array.</summary>
    public int Count => _protoArray.Nodes.Count;

    public int PruneThreshold
    {
        get => _protoArray.PruneThreshold;
        set => _protoArray.PruneThreshold = value;
    }

    /// <summary>Records the latest message of a validator, keeping only the vote with the highest target epoch.</summary>
    public void ProcessAttestation(ulong validatorIndex, Hash256 blockRoot, ulong targetEpoch)
    {
        ref VoteTracker vote = ref _votes.GetMut(validatorIndex);
        if (targetEpoch > vote.NextEpoch || vote.IsUnset)
        {
            vote.NextRoot = blockRoot;
            vote.NextEpoch = targetEpoch;
        }
    }

    /// <summary>Registers a block with the fork choice; see <see cref="ProtoArray.OnBlock"/>.</summary>
    /// <exception cref="ProtoArrayException">The block has no parent root, or its parent is invalid.</exception>
    public void ProcessBlock(ProtoBlock block, ulong currentSlot, CheckpointRef justifiedCheckpoint, CheckpointRef finalizedCheckpoint)
    {
        if (block.ParentRoot is null)
        {
            throw new ProtoArrayException($"Block {block.Root} is missing its parent root");
        }

        _protoArray.OnBlock(block, currentSlot, justifiedCheckpoint, finalizedCheckpoint);
    }

    /// <summary>Applies pending vote and balance deltas, then finds the head from the justified root.</summary>
    /// <param name="equivocatingIndices">Validators whose votes must be discounted; when <c>null</c>, the indices accumulated via <see cref="OnAttesterSlashing"/> are used.</param>
    public Hash256 GetHead(
        CheckpointRef justifiedCheckpoint,
        CheckpointRef finalizedCheckpoint,
        JustifiedBalances justifiedBalances,
        IReadOnlySet<ulong>? equivocatingIndices,
        ulong currentSlot)
    {
        long[] deltas = _votes.ComputeDeltas(
            _protoArray.Indices,
            _balances.EffectiveBalances,
            justifiedBalances.EffectiveBalances,
            equivocatingIndices ?? _equivocatingIndices);

        _protoArray.ApplyScoreChanges(deltas, justifiedCheckpoint, finalizedCheckpoint, justifiedBalances, ProposerBoostRoot, currentSlot);

        _balances = justifiedBalances;

        return _protoArray.FindHead(justifiedCheckpoint.Root, currentSlot, justifiedCheckpoint, finalizedCheckpoint);
    }

    /// <summary>Boosts the block that arrived in time during the current slot; reset at the start of every slot.</summary>
    public void SetProposerBoostRoot(Hash256 proposerBoostRoot) => ProposerBoostRoot = proposerBoostRoot;

    public void ResetProposerBoostRoot() => ProposerBoostRoot = Hash256.Zero;

    /// <summary>Registers equivocating validators; their votes are deducted on the next <see cref="GetHead"/> and never counted again.</summary>
    public void OnAttesterSlashing(IEnumerable<ulong> validatorIndices)
    {
        foreach (ulong validatorIndex in validatorIndices)
        {
            _equivocatingIndices.Add(validatorIndex);
        }
    }

    /// <inheritdoc cref="ProtoArray.PropagateExecutionValidation(Hash256)"/>
    public void ProcessExecutionPayloadValidation(Hash256 blockRoot) => _protoArray.PropagateExecutionValidation(blockRoot);

    /// <inheritdoc cref="ProtoArray.InvalidateBlock"/>
    public void ProcessExecutionPayloadInvalidation(InvalidationOperation op, CheckpointRef finalizedCheckpoint) =>
        _protoArray.InvalidateBlock(op, finalizedCheckpoint);

    /// <inheritdoc cref="ProtoArray.Prune"/>
    public void MaybePrune(Hash256 finalizedRoot) => _protoArray.Prune(finalizedRoot);

    public bool ContainsBlock(Hash256 blockRoot) => _protoArray.Indices.ContainsKey(blockRoot);

    public ulong? GetWeight(Hash256 blockRoot) =>
        _protoArray.Indices.TryGetValue(blockRoot, out int index) ? _protoArray.Nodes[index].Weight : null;

    public ulong? GetBlockSlot(Hash256 blockRoot) =>
        _protoArray.Indices.TryGetValue(blockRoot, out int index) ? _protoArray.Nodes[index].Slot : null;

    /// <summary>
    /// Returns the ancestor of <paramref name="blockRoot"/> at or before <paramref name="slot"/>
    /// (the spec's <c>get_ancestor</c>), or <c>null</c> when the block is unknown.
    /// </summary>
    public Hash256? GetAncestor(Hash256 blockRoot, ulong slot)
    {
        foreach (ProtoNode node in _protoArray.EnumerateAncestorNodes(blockRoot))
        {
            if (node.Slot <= slot) return node.Root;
        }

        return null;
    }

    public ExecutionStatus? GetBlockExecutionStatus(Hash256 blockRoot) =>
        _protoArray.Indices.TryGetValue(blockRoot, out int index) ? _protoArray.Nodes[index].ExecutionStatus : null;

    /// <inheritdoc cref="VoteTrackerList.LatestMessage"/>
    public (Hash256 BlockRoot, ulong TargetEpoch)? LatestMessage(ulong validatorIndex) => _votes.LatestMessage(validatorIndex);

    /// <inheritdoc cref="ProtoArray.IsDescendant"/>
    public bool IsDescendant(Hash256 ancestorRoot, Hash256 descendantRoot) => _protoArray.IsDescendant(ancestorRoot, descendantRoot);

    /// <inheritdoc cref="ProtoArray.IsFinalizedCheckpointOrDescendant"/>
    public bool IsFinalizedCheckpointOrDescendant(Hash256 root, CheckpointRef finalizedCheckpoint) =>
        _protoArray.IsFinalizedCheckpointOrDescendant(root, finalizedCheckpoint);
}
