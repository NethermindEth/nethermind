// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// The proto-array LMD-GHOST block tree: nodes stored in insertion order (children always after
/// parents) with cached best-child/best-descendant links. Ported from Lighthouse's <c>ProtoArray</c>.
/// </summary>
/// <param name="slotsPerEpoch">Number of slots per epoch (32 on mainnet).</param>
/// <param name="proposerScoreBoostPercent">The proposer boost as a percentage of a single committee's weight (<c>PROPOSER_SCORE_BOOST</c>, 40 on mainnet).</param>
public sealed class ProtoArray(ulong slotsPerEpoch, ulong proposerScoreBoostPercent)
{
    private const ulong GenesisEpoch = 0;

    private Hash256 _previousProposerBoostRoot = Hash256.Zero;
    private ulong _previousProposerBoostScore;

    /// <summary>Do not prune unless the finalized block is at least this deep; small prunes waste time.</summary>
    public int PruneThreshold { get; set; } = ProtoArrayForkChoice.DefaultPruneThreshold;

    public List<ProtoNode> Nodes { get; } = [];

    public Dictionary<Hash256, int> Indices { get; } = [];

    /// <summary>
    /// Applies <paramref name="deltas"/> to all node weights and refreshes the
    /// best-child/best-descendant links.
    /// </summary>
    /// <remarks>
    /// Two backward passes over the nodes: the first applies each node's delta (replacing it with a
    /// weight-zeroing delta for invalid nodes, and adding/removing the proposer boost), then
    /// back-propagates the delta into the parent's slot of <paramref name="deltas"/>; the second
    /// updates the parents' best-child/best-descendant once all weights are coherent. Weight
    /// arithmetic is checked and fails fast, matching Lighthouse's <c>checked_add</c>/<c>checked_sub</c>.
    /// </remarks>
    public void ApplyScoreChanges(
        long[] deltas,
        CheckpointRef newJustifiedCheckpoint,
        CheckpointRef newFinalizedCheckpoint,
        JustifiedBalances newJustifiedBalances,
        Hash256 proposerBoostRoot,
        ulong currentSlot)
    {
        if (deltas.Length != Indices.Count)
        {
            throw new ProtoArrayException($"Invalid delta length: {deltas.Length}, expected {Indices.Count}");
        }

        ulong proposerScore = 0;

        for (int nodeIndex = Nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            ProtoNode node = Nodes[nodeIndex];

            // The zero hash is an alias for the genesis block: it is always chosen and can have no
            // parent, so its weight is irrelevant.
            if (node.Root == Hash256.Zero) continue;

            bool executionStatusIsInvalid = node.ExecutionStatus == ExecutionStatus.Invalid;

            // A node with an invalid execution payload has its weight forced to zero.
            long nodeDelta = executionStatusIsInvalid ? checked(0 - (long)node.Weight) : deltas[nodeIndex];

            // Remove the boost previously applied to this node. Invalid nodes already have a
            // weight-zeroing delta, so there is nothing to subtract.
            if (_previousProposerBoostRoot != Hash256.Zero && _previousProposerBoostRoot == node.Root && !executionStatusIsInvalid)
            {
                nodeDelta = checked(nodeDelta - (long)_previousProposerBoostScore);
            }

            // Apply the new proposer boost; invalid nodes (and hence their ancestors) never receive it.
            // https://github.com/ethereum/consensus-specs/blob/dev/specs/phase0/fork-choice.md#get_weight
            if (proposerBoostRoot != Hash256.Zero && proposerBoostRoot == node.Root && !executionStatusIsInvalid)
            {
                proposerScore = CalculateCommitteeFraction(newJustifiedBalances, proposerScoreBoostPercent);
                nodeDelta = checked(nodeDelta + (long)proposerScore);
            }

            if (executionStatusIsInvalid)
            {
                node.Weight = 0;
            }
            else if (nodeDelta < 0)
            {
                // unchecked negation is exact even for long.MinValue: the wrapped value reinterpreted
                // as ulong is the magnitude.
                ulong magnitude = unchecked((ulong)(-nodeDelta));
                node.Weight = magnitude > node.Weight
                    ? throw new ProtoArrayException($"Delta overflow for node {nodeIndex}: weight {node.Weight}, delta {nodeDelta}")
                    : node.Weight - magnitude;
            }
            else
            {
                node.Weight = checked(node.Weight + (ulong)nodeDelta);
            }

            if (node.Parent is int parentIndex)
            {
                deltas[parentIndex] = checked(deltas[parentIndex] + nodeDelta);
            }
        }

        _previousProposerBoostRoot = proposerBoostRoot;
        _previousProposerBoostScore = proposerScore;

        // A second backward pass, separate from the weight-updating loop above, so the
        // best-child/best-descendant updates see a fully coherent set of weights.
        for (int nodeIndex = Nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            if (Nodes[nodeIndex].Parent is int parentIndex)
            {
                MaybeUpdateBestChildAndDescendant(parentIndex, nodeIndex, currentSlot, newJustifiedCheckpoint, newFinalizedCheckpoint);
            }
        }
    }

    /// <summary>Registers a block with the fork choice; already-known blocks are ignored.</summary>
    /// <remarks>Only the anchor (root) block may have a <c>null</c> or unknown parent.</remarks>
    /// <exception cref="ProtoArrayException">The parent has an invalid execution payload.</exception>
    public void OnBlock(ProtoBlock block, ulong currentSlot, CheckpointRef justifiedCheckpoint, CheckpointRef finalizedCheckpoint)
    {
        if (Indices.ContainsKey(block.Root)) return;

        if ((block.ExecutionStatus == ExecutionStatus.Irrelevant) != (block.ExecutionBlockHash is null))
        {
            throw new ProtoArrayException($"Block {block.Root} must carry an execution block hash if and only if execution is enabled");
        }

        int nodeIndex = Nodes.Count;
        int? parentIndex = block.ParentRoot is not null && Indices.TryGetValue(block.ParentRoot, out int parent) ? parent : null;

        if (parentIndex is int checkedParent && Nodes[checkedParent].ExecutionStatus == ExecutionStatus.Invalid)
        {
            throw new ProtoArrayException($"Parent {Nodes[checkedParent].Root} of block {block.Root} has an invalid execution payload");
        }

        ProtoNode node = new()
        {
            Slot = block.Slot,
            Root = block.Root,
            TargetRoot = block.TargetRoot,
            StateRoot = block.StateRoot,
            Parent = parentIndex,
            JustifiedCheckpoint = block.JustifiedCheckpoint,
            FinalizedCheckpoint = block.FinalizedCheckpoint,
            Weight = 0,
            BestChild = null,
            BestDescendant = null,
            ExecutionStatus = block.ExecutionStatus,
            ExecutionBlockHash = block.ExecutionBlockHash,
            UnrealizedJustifiedCheckpoint = block.UnrealizedJustifiedCheckpoint,
            UnrealizedFinalizedCheckpoint = block.UnrealizedFinalizedCheckpoint,
        };

        Indices[node.Root] = nodeIndex;
        Nodes.Add(node);

        if (parentIndex is int parentNodeIndex)
        {
            MaybeUpdateBestChildAndDescendant(parentNodeIndex, nodeIndex, currentSlot, justifiedCheckpoint, finalizedCheckpoint);

            if (block.ExecutionStatus == ExecutionStatus.Valid)
            {
                PropagateExecutionValidation(parentNodeIndex);
            }
        }
    }

    /// <summary>Marks <paramref name="blockRoot"/> and all its ancestors as having valid execution payloads.</summary>
    /// <exception cref="ProtoArrayException">The block is unknown, or an ancestor is already invalid (a consensus failure of the execution layer).</exception>
    public void PropagateExecutionValidation(Hash256 blockRoot)
    {
        if (!Indices.TryGetValue(blockRoot, out int index))
        {
            throw new ProtoArrayException($"Block {blockRoot} unknown to fork choice");
        }

        PropagateExecutionValidation(index);
    }

    private void PropagateExecutionValidation(int verifiedNodeIndex)
    {
        int index = verifiedNodeIndex;
        while (true)
        {
            ProtoNode node = Nodes[index];
            switch (node.ExecutionStatus)
            {
                // Already valid: all ancestors are assumed valid too. Irrelevant: the node precedes
                // the terminal execution block, so no ancestor can be relevant either.
                case ExecutionStatus.Valid:
                case ExecutionStatus.Irrelevant:
                    return;
                case ExecutionStatus.Optimistic:
                    // Any ancestor of a valid payload is valid.
                    node.ExecutionStatus = ExecutionStatus.Valid;
                    if (node.Parent is not int parentIndex) return;
                    index = parentIndex;
                    break;
                case ExecutionStatus.Invalid:
                    // An invalid ancestor of a valid payload is an unrecoverable consensus failure
                    // in the execution node.
                    throw new ProtoArrayException($"Block {node.Root} (payload {node.ExecutionBlockHash}) is an invalid ancestor of a valid payload");
            }
        }
    }

    /// <summary>Invalidates zero or more blocks as specified by <paramref name="op"/>; Lighthouse's <c>propagate_execution_payload_invalidation</c>.</summary>
    /// <exception cref="ProtoArrayException">The head block is unknown, or a previously-valid payload would become invalid.</exception>
    public void InvalidateBlock(InvalidationOperation op, CheckpointRef finalizedCheckpoint)
    {
        HashSet<int> invalidatedIndices = [];
        Hash256 headBlockRoot = op.HeadBlockRoot;

        // Step 1: find the head block, then iterate backwards invalidating ancestors up to (but
        // excluding) the latest valid ancestor.
        if (!Indices.TryGetValue(headBlockRoot, out int index))
        {
            throw new ProtoArrayException($"Block {headBlockRoot} unknown to fork choice");
        }

        // Try to map the ancestor payload *hash* to an ancestor beacon block *root*.
        Hash256? latestValidAncestorRoot = op.LatestValidAncestor is not null
            ? ExecutionBlockHashToBeaconBlockRoot(op.LatestValidAncestor)
            : null;

        // True only if the head descends from the latest valid ancestor, which itself is the
        // finalized checkpoint or one of its descendants.
        bool latestValidAncestorIsDescendant = latestValidAncestorRoot is not null
            && IsDescendant(latestValidAncestorRoot, headBlockRoot)
            && IsFinalizedCheckpointOrDescendant(latestValidAncestorRoot, finalizedCheckpoint);

        while (true)
        {
            ProtoNode node = Nodes[index];

            // Pre-merge nodes (and their ancestors) have no execution status to invalidate.
            if (node.ExecutionStatus == ExecutionStatus.Irrelevant) break;

            // If an unknown hash (junk or pre-finalization) was supplied, don't touch any
            // ancestors. The alternative is invalidating *all* ancestors, which would likely mean
            // shutting down the client due to an invalid justified checkpoint.
            if (!latestValidAncestorIsDescendant && node.Root != headBlockRoot) break;

            if (op.LatestValidAncestor is not null && node.ExecutionBlockHash == op.LatestValidAncestor)
            {
                // The latest valid ancestor itself: if its best child/descendant was invalidated,
                // clear those fields. An invalid best child implies an invalid best descendant, but
                // each is checked independently to defend against an invalid block becoming head.
                if (node.BestChild is int bestChild && invalidatedIndices.Contains(bestChild))
                {
                    node.BestChild = null;
                }

                if (node.BestDescendant is int bestDescendant && invalidatedIndices.Contains(bestDescendant))
                {
                    node.BestDescendant = null;
                }

                break;
            }

            // Only invalidate the head block itself if it was specifically indicated for
            // invalidation or the latest valid hash is a known ancestor.
            if (node.Root != headBlockRoot || op.InvalidateBlockRoot || latestValidAncestorIsDescendant)
            {
                switch (node.ExecutionStatus)
                {
                    case ExecutionStatus.Valid:
                        // An execution client declaring a previously-valid block invalid is a
                        // consensus failure on its behalf.
                        throw new ProtoArrayException($"Valid execution status of block {node.Root} (payload {node.ExecutionBlockHash}) became invalid");
                    case ExecutionStatus.Optimistic:
                        invalidatedIndices.Add(index);
                        node.ExecutionStatus = ExecutionStatus.Invalid;

                        // An invalid block can never lead to a "best" block. Failing to clear these
                        // would make NodeLeadsToViableHead return false for *valid* ancestors of
                        // invalid blocks.
                        node.BestChild = null;
                        node.BestDescendant = null;
                        break;
                    case ExecutionStatus.Invalid:
                        // Already invalid; keep going backwards so all ancestors are updated.
                        break;
                }
            }

            if (node.Parent is not int parentIndex)
            {
                // Reached the root of the block tree without matching the latest valid ancestor:
                // nothing further back can usefully be invalidated.
                break;
            }

            index = parentIndex;
        }

        // Step 2: iterate *forwards* from the latest valid ancestor (or the head block) and
        // invalidate all descendants of the blocks invalidated above.
        Hash256 startingBlockRoot = latestValidAncestorIsDescendant && latestValidAncestorRoot is not null
            ? latestValidAncestorRoot
            : headBlockRoot;
        if (!Indices.TryGetValue(startingBlockRoot, out int startingIndex))
        {
            throw new ProtoArrayException($"Block {startingBlockRoot} unknown to fork choice");
        }

        for (int descendantIndex = startingIndex + 1; descendantIndex < Nodes.Count; descendantIndex++)
        {
            ProtoNode node = Nodes[descendantIndex];
            if (node.Parent is not int parentIndex || !invalidatedIndices.Contains(parentIndex)) continue;

            switch (node.ExecutionStatus)
            {
                case ExecutionStatus.Valid:
                    throw new ProtoArrayException($"Valid execution status of block {node.Root} (payload {node.ExecutionBlockHash}) became invalid");
                case ExecutionStatus.Optimistic:
                case ExecutionStatus.Invalid:
                    node.ExecutionStatus = ExecutionStatus.Invalid;
                    break;
                case ExecutionStatus.Irrelevant:
                    throw new ProtoArrayException($"Irrelevant block {node.Root} is a descendant of a block with an execution payload");
            }

            invalidatedIndices.Add(descendantIndex);
        }
    }

    /// <summary>Follows the best-descendant links from <paramref name="justifiedRoot"/> to find the head block root.</summary>
    /// <remarks>
    /// The result is only accurate if <see cref="ApplyScoreChanges"/> has been called since the last
    /// <see cref="OnBlock"/>, since <see cref="OnBlock"/> does not walk backwards updating
    /// best-child/best-descendant links.
    /// </remarks>
    /// <exception cref="ProtoArrayException">The justified root is unknown or invalid, or the best node is not viable for the head.</exception>
    public Hash256 FindHead(Hash256 justifiedRoot, ulong currentSlot, CheckpointRef justifiedCheckpoint, CheckpointRef finalizedCheckpoint)
    {
        if (!Indices.TryGetValue(justifiedRoot, out int justifiedIndex))
        {
            throw new ProtoArrayException($"Justified block {justifiedRoot} unknown to fork choice");
        }

        ProtoNode justifiedNode = Nodes[justifiedIndex];

        // A justified block with an invalid execution payload has no valid descendants to choose
        // from. Fork choice is broken until a new justified root is set; this is an unsupported,
        // serious consensus failure.
        if (justifiedNode.ExecutionStatus == ExecutionStatus.Invalid)
        {
            throw new ProtoArrayException($"Justified block {justifiedRoot} has an invalid execution payload");
        }

        ProtoNode bestNode = Nodes[justifiedNode.BestDescendant ?? justifiedIndex];

        if (!NodeIsViableForHead(bestNode, currentSlot, justifiedCheckpoint, finalizedCheckpoint))
        {
            throw new ProtoArrayException(
                $"Best node {bestNode.Root} (justified {bestNode.JustifiedCheckpoint}, finalized {bestNode.FinalizedCheckpoint}) " +
                $"is not viable for head at slot {currentSlot} from {justifiedRoot} " +
                $"(justified {justifiedCheckpoint}, finalized {finalizedCheckpoint})");
        }

        return bestNode.Root;
    }

    /// <summary>Drops all nodes preceding <paramref name="finalizedRoot"/>, unless it is shallower than <see cref="PruneThreshold"/>.</summary>
    /// <exception cref="ProtoArrayException">The finalized root is unknown, or an internal index is inconsistent.</exception>
    public void Prune(Hash256 finalizedRoot)
    {
        if (!Indices.TryGetValue(finalizedRoot, out int finalizedIndex))
        {
            throw new ProtoArrayException($"Finalized block {finalizedRoot} unknown to fork choice");
        }

        // Pruning small numbers of nodes costs more than it saves.
        if (finalizedIndex < PruneThreshold) return;

        for (int nodeIndex = 0; nodeIndex < finalizedIndex; nodeIndex++)
        {
            Indices.Remove(Nodes[nodeIndex].Root);
        }

        Nodes.RemoveRange(0, finalizedIndex);

        List<Hash256> remainingRoots = [.. Indices.Keys];
        foreach (Hash256 root in remainingRoots)
        {
            int adjusted = Indices[root] - finalizedIndex;
            Indices[root] = adjusted >= 0 ? adjusted : throw new ProtoArrayException($"Index underflow for {root} during prune");
        }

        foreach (ProtoNode node in Nodes)
        {
            // A parent before the finalized block has been pruned away.
            node.Parent = node.Parent is int parent && parent >= finalizedIndex ? parent - finalizedIndex : null;

            if (node.BestChild is int bestChild)
            {
                node.BestChild = bestChild >= finalizedIndex
                    ? bestChild - finalizedIndex
                    : throw new ProtoArrayException($"Best child of {node.Root} underflows during prune");
            }

            if (node.BestDescendant is int bestDescendant)
            {
                node.BestDescendant = bestDescendant >= finalizedIndex
                    ? bestDescendant - finalizedIndex
                    : throw new ProtoArrayException($"Best descendant of {node.Root} underflows during prune");
            }
        }
    }

    /// <summary>
    /// Observes the child at <paramref name="childIndex"/> and potentially updates the best-child
    /// and best-descendant of its parent at <paramref name="parentIndex"/>.
    /// </summary>
    /// <remarks>
    /// Four outcomes: the child is the best child but is no longer viable and is removed; the child
    /// is the best child and the parent's best-descendant is refreshed; the child becomes the best
    /// child (viability, then weight, with ties broken by the higher root); or nothing changes.
    /// </remarks>
    private void MaybeUpdateBestChildAndDescendant(
        int parentIndex,
        int childIndex,
        ulong currentSlot,
        CheckpointRef justifiedCheckpoint,
        CheckpointRef finalizedCheckpoint)
    {
        ProtoNode child = Nodes[childIndex];
        ProtoNode parent = Nodes[parentIndex];

        bool childLeadsToViableHead = NodeLeadsToViableHead(child, currentSlot, justifiedCheckpoint, finalizedCheckpoint);

        (int? BestChild, int? BestDescendant) changeToNone = (null, null);
        (int? BestChild, int? BestDescendant) changeToChild = (childIndex, child.BestDescendant ?? childIndex);
        (int? BestChild, int? BestDescendant) noChange = (parent.BestChild, parent.BestDescendant);

        (int? newBestChild, int? newBestDescendant) = parent.BestChild switch
        {
            int bestChildIndex when bestChildIndex == childIndex =>
                childLeadsToViableHead ? changeToChild : changeToNone,
            int bestChildIndex => CompareToCurrentBest(bestChildIndex),
            null => childLeadsToViableHead ? changeToChild : noChange,
        };

        parent.BestChild = newBestChild;
        parent.BestDescendant = newBestDescendant;

        (int?, int?) CompareToCurrentBest(int bestChildIndex)
        {
            ProtoNode bestChild = Nodes[bestChildIndex];
            bool bestChildLeadsToViableHead = NodeLeadsToViableHead(bestChild, currentSlot, justifiedCheckpoint, finalizedCheckpoint);

            if (childLeadsToViableHead && !bestChildLeadsToViableHead) return changeToChild;
            if (!childLeadsToViableHead && bestChildLeadsToViableHead) return noChange;

            if (child.Weight == bestChild.Weight)
            {
                // Tie-break equal weights by the higher root.
                return child.Root.CompareTo(bestChild.Root) >= 0 ? changeToChild : noChange;
            }

            return child.Weight > bestChild.Weight ? changeToChild : noChange;
        }
    }

    /// <summary>Indicates if the node itself, or its best descendant, is viable for the head.</summary>
    private bool NodeLeadsToViableHead(ProtoNode node, ulong currentSlot, CheckpointRef justifiedCheckpoint, CheckpointRef finalizedCheckpoint)
    {
        bool bestDescendantIsViableForHead = node.BestDescendant is int bestDescendantIndex
            && NodeIsViableForHead(Nodes[bestDescendantIndex], currentSlot, justifiedCheckpoint, finalizedCheckpoint);

        return bestDescendantIsViableForHead || NodeIsViableForHead(node, currentSlot, justifiedCheckpoint, finalizedCheckpoint);
    }

    /// <summary>
    /// The per-node filter of <c>filter_block_tree</c> from the consensus spec: a node whose voting
    /// source conflicts with the justified checkpoint, or which conflicts with finality, is not
    /// viable for the head.
    /// </summary>
    /// <remarks>
    /// https://github.com/ethereum/consensus-specs/blob/dev/specs/phase0/fork-choice.md#filter_block_tree
    /// For blocks from a prior epoch the voting source is "pulled up" to the unrealized justified
    /// checkpoint when it is tracked.
    /// </remarks>
    private bool NodeIsViableForHead(ProtoNode node, ulong currentSlot, CheckpointRef justifiedCheckpoint, CheckpointRef finalizedCheckpoint)
    {
        if (node.ExecutionStatus == ExecutionStatus.Invalid) return false;

        ulong currentEpoch = currentSlot / slotsPerEpoch;
        ulong nodeEpoch = node.Slot / slotsPerEpoch;

        CheckpointRef votingSource = currentEpoch > nodeEpoch
            ? node.UnrealizedJustifiedCheckpoint ?? node.JustifiedCheckpoint
            : node.JustifiedCheckpoint;

        bool correctJustified = justifiedCheckpoint.Epoch == GenesisEpoch
            || votingSource.Epoch == justifiedCheckpoint.Epoch
            || votingSource.Epoch + 2 >= currentEpoch;

        bool correctFinalized = finalizedCheckpoint.Epoch == GenesisEpoch
            || IsFinalizedCheckpointOrDescendant(node.Root, finalizedCheckpoint);

        return correctJustified && correctFinalized;
    }

    /// <summary>Enumerates the chain of nodes ending at <paramref name="blockRoot"/>, from the block backwards to the tree root.</summary>
    /// <remarks>Yields nothing if <paramref name="blockRoot"/> is unknown. Skipped slots yield no entries.</remarks>
    public IEnumerable<ProtoNode> EnumerateAncestorNodes(Hash256 blockRoot)
    {
        int? nextNodeIndex = Indices.TryGetValue(blockRoot, out int index) ? index : null;
        while (nextNodeIndex is int nodeIndex)
        {
            ProtoNode node = Nodes[nodeIndex];
            yield return node;
            nextNodeIndex = node.Parent;
        }
    }

    /// <summary>Returns <c>true</c> if <paramref name="descendantRoot"/> is <paramref name="ancestorRoot"/> or one of its descendants.</summary>
    /// <remarks>
    /// Returns <c>false</c> if either root is unknown. Do not use this to check descent from the
    /// finalized checkpoint — use <see cref="IsFinalizedCheckpointOrDescendant"/>, which also handles
    /// a finalized checkpoint at a skipped slot.
    /// </remarks>
    public bool IsDescendant(Hash256 ancestorRoot, Hash256 descendantRoot)
    {
        if (!Indices.TryGetValue(ancestorRoot, out int ancestorIndex)) return false;

        ulong ancestorSlot = Nodes[ancestorIndex].Slot;
        foreach (ProtoNode node in EnumerateAncestorNodes(descendantRoot))
        {
            if (node.Slot < ancestorSlot) return false;
            if (node.Slot == ancestorSlot) return node.Root == ancestorRoot;
        }

        return false;
    }

    /// <summary>Returns <c>true</c> if <paramref name="root"/> is equal to or a descendant of the finalized <em>checkpoint</em> (not merely the finalized block).</summary>
    public bool IsFinalizedCheckpointOrDescendant(Hash256 root, CheckpointRef finalizedCheckpoint)
    {
        Hash256 finalizedRoot = finalizedCheckpoint.Root;
        ulong finalizedSlot = checked(finalizedCheckpoint.Epoch * slotsPerEpoch);

        // An unknown root is not a finalized descendant.
        if (!Indices.TryGetValue(root, out int index)) return false;

        ProtoNode node = Nodes[index];

        // The node's own checkpoints are known ancestors likely to coincide with the store's
        // finalized checkpoint; check them once here rather than walking the whole chain.
        if (node.FinalizedCheckpoint == finalizedCheckpoint
            || node.JustifiedCheckpoint == finalizedCheckpoint
            || node.UnrealizedFinalizedCheckpoint == finalizedCheckpoint
            || node.UnrealizedJustifiedCheckpoint == finalizedCheckpoint)
        {
            return true;
        }

        while (true)
        {
            // At or below the finalized slot the node must be the finalized block itself.
            if (node.Slot <= finalizedSlot) return node.Root == finalizedRoot;

            if (node.Parent is not int parentIndex)
            {
                // Proto-array only prunes blocks prior to the finalized block, so a missing parent
                // above the finalized slot means the chain conflicts with finality.
                return false;
            }

            node = Nodes[parentIndex];
        }
    }

    /// <summary>Returns the latest beacon block root whose execution payload hash is <paramref name="blockHash"/>, if any.</summary>
    public Hash256? ExecutionBlockHashToBeaconBlockRoot(Hash256 blockHash)
    {
        for (int nodeIndex = Nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            if (Nodes[nodeIndex].ExecutionBlockHash == blockHash) return Nodes[nodeIndex].Root;
        }

        return null;
    }

    /// <summary>The proposer boost in Gwei: <paramref name="boostPercent"/>% of one committee's share of the total active balance.</summary>
    /// <remarks>https://github.com/ethereum/consensus-specs/blob/dev/specs/phase0/fork-choice.md#get_proposer_score</remarks>
    public ulong CalculateCommitteeFraction(JustifiedBalances justifiedBalances, ulong boostPercent) =>
        checked(justifiedBalances.TotalEffectiveBalance / slotsPerEpoch * boostPercent / 100);
}
