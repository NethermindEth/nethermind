// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private sealed partial class Updater
    {
        /// <summary>
        /// Builds the run of single-child levels from <paramref name="startDepth"/> down to the group at
        /// <paramref name="targetDepth"/>, whose subtree amounts to <paramref name="stats"/>, in memory of
        /// its own: until an ancestor writes it into its own encoding, no group's holds it.
        /// </summary>
        private Occupant NewChainNode(
            int startDepth, int targetDepth, in Stem targetPath, in ValueHash256 targetHash, in PbtSubtreeStats stats)
        {
            RefCountingMemory memory = memoryProvider.Rent(PbtNodeChain.EncodedLength);
            PbtNodeChain.Write(memory.GetSpan(), startDepth, targetDepth, targetPath, targetHash, stats);
            return new Occupant(new NodeRef(NodeKind.Chain, 0), memory);
        }

        /// <summary>
        /// Copies an unchanged run's encoding out of the group holding it into memory of its own, for the
        /// group that replaces it — or an enclosing one absorbing it — to take over.
        /// </summary>
        private NodeResult CopyChainNode(ReadOnlySpan<byte> chainData)
        {
            Debug.Assert(chainData.Length == PbtNodeChain.EncodedLength);
            RefCountingMemory memory = memoryProvider.Rent(PbtNodeChain.EncodedLength);
            chainData.CopyTo(memory.GetSpan());
            return NodeResult.Chain(memory);
        }

        /// <summary>
        /// Builds the boundary internal caching <paramref name="hash"/> in memory of its own, for a
        /// pointer to a stored group that no group's encoding holds yet.
        /// </summary>
        private Occupant NewInternalNode(in ValueHash256 hash)
        {
            RefCountingMemory memory = memoryProvider.Rent(PbtTrieNodeGroup.Slot.InternalLength);
            hash.Bytes.CopyTo(memory.GetSpan());
            return new Occupant(new NodeRef(NodeKind.Internal, 0), memory);
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to the run of single-child levels at <paramref name="key"/>,
        /// whose content is <paramref name="chainData"/> — the <see cref="PbtNodeChain"/> encoding the
        /// parent group holds at this slot, or the rest of a run an enclosing frame is descending.
        /// </summary>
        /// <remarks>
        /// The run holds no node between its start and its target, so nothing here walks it level by
        /// level: the entries' shallowest departure from its path says where the trie next branches, and
        /// the descent resumes at the group holding that bit — or at the target itself, when they all pass
        /// through. The inner frame gives up this one's precalculated bounds: such a level describes one
        /// range at one depth, and skipping levels is exactly what leaves it describing neither — no loss, as
        /// a run only ever reaches past the topmost levels a producer buckets. Where the entries part is a
        /// property of the entries rather than of a depth, so that much does carry over the jump.
        /// </remarks>
        /// <param name="plan"><inheritdoc cref="ResolveBoundaries" path="/param[@name='plan']"/></param>
        /// <param name="writer"><inheritdoc cref="ApplyGroup" path="/param[@name='writer']"/></param>
        /// <param name="result"><inheritdoc cref="ApplyGroup" path="/param[@name='result']"/></param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private void ApplyChain(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, ReadOnlySpan<byte> chainData,
            scoped BucketPlan plan, ref BufferWriter writer, out NodeResult result,
            out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty);

            PbtNodeChain chain = PbtNodeChain.Decode(chainData, depth);
            int targetDepth = chain.TargetDepth;
            Stem targetPath = chain.TargetPath;

            // The shallowest bit at which a write leaves the run. FirstDifferingBit reports -1 for a stem
            // that never parts from the path, which as an unsigned compare reads as past everything —
            // which is what never parting means: the write descends the whole run.
            int first = entries[0].Stem.FirstDifferingBit(targetPath, depth);
            int splitDepth = (uint)first < (uint)targetDepth ? first : targetDepth;

            // The entries agree with one another down to their branch depth, so where that reaches past
            // where the first of them leaves the run — or past the run itself — the first entry's departure
            // is the whole range's: any other agrees with it there, so it leaves the path in the same place.
            // The corridor a batch of one contract's storage slots descends is exactly that case, and this
            // is the run's only walk over the range.
            if ((uint)first >= (uint)plan.BranchDepth && plan.BranchDepth < targetDepth)
            {
                for (int i = 1; i < entries.Length; i++)
                {
                    int diff = entries[i].Stem.FirstDifferingBit(targetPath, depth);
                    if ((uint)diff < (uint)splitDepth) splitDepth = diff;
                }
            }

            Debug.Assert(splitDepth >= depth, "the entries share the run's path down to this frame");

            // The writes part from the run nowhere, so they descend the whole of it into its target group —
            // the one blob a run points at (invariant 2). Apply there and fold the result back into a run
            // starting here.
            if (splitDepth == targetDepth)
            {
                using RefCountingMemory? targetData = store.GetTrieNode(chain.TargetKey);
                BufferWriter targetWriter = new(memoryProvider, targetData?.GetSpan().Length ?? 0);
                NodeResult inner;
                bool targetChanged;
                if (PbtLayout.IsClusteringDepth(targetDepth))
                {
                    ApplyClustered(chain.TargetKey, entries, StoredBlob.Of(targetData), chain.TargetHash, plan.AfterJump(), ref targetWriter, out inner, out targetChanged, out delta);
                }
                else
                {
                    ApplyGroup(chain.TargetKey, entries, StoredBlob.Of(targetData), chain.TargetHash, plan.AfterJump(), ref targetWriter, out inner, out targetChanged, out delta);
                }

                if (targetWriter.Detach() is { } targetBlob) inner = inner.WithBlob(targetBlob);

                // The run points at one group and one only (invariant 2). When the writes leave that group
                // byte-identical, the run above it is unchanged too — its node hash folds from the target's,
                // which did not move — so its encoding still stands: hand it up verbatim and skip the fold
                // WrapIntoChain would spend to recompute a hash already cached.
                if (!targetChanged)
                {
                    inner.Dispose();
                    Debug.Assert(delta.IsZero, "an unchanged target subtree moves no statistic");
                    result = CopyChainNode(chainData);
                    changed = false;
                    return;
                }

                result = WrapIntoChain(depth, chain.TargetKey, inner, targetData is not null, chain.Stats + delta);
                changed = result.NodeHash() != chain.NodeHash;
                return;
            }

            // Otherwise the writes branch inside the run, so the group holding the branch becomes real.
            // Split it there — this very frame when the branch falls within its own four levels
            // (branchDepth == depth ⇔ splitDepth − depth < LevelsPerGroup), else the deeper group — and let
            // ApplyChainSplit fold the split back into the run prefix reaching down to it. Nothing is stored
            // along that prefix (invariant 3), so the deeper group is unbucketed and partitions for itself.
            // A deeper group has a run minted above it, so it is one no blob of this frame's holds: it owns
            // its own, however the run this frame replaces was held.
            int branchDepth = splitDepth & ~(PbtLayout.TrieNodeGroupLevelsPerGroup - 1);
            bool branchesHere = branchDepth == depth;
            if (branchesHere)
            {
                result = ApplyChainSplit(key, entries, chain, plan, ref writer, out changed, out delta);
                return;
            }

            // The branch fell in a deeper group than the run's start, so the split node hangs below a run
            // prefix reaching down to it, which holds no node of its own (invariant 3). That prefixed run's
            // hash — not the split group's — is what changed against the run this frame replaced.
            TrieNodeKey branchKey = TrieNodeKey.For(branchDepth, targetPath);
            BufferWriter splitWriter = new(memoryProvider);
            NodeResult split = ApplyChainSplit(branchKey, entries, chain, plan.AfterJump(), ref splitWriter, out _, out delta);
            if (splitWriter.Detach() is { } splitBlob) split = split.WithBlob(splitBlob);
            result = WrapIntoChain(depth, branchKey, split, innerBlobStored: false, chain.Stats + delta);
            changed = result.NodeHash() != chain.NodeHash;
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to a run that branches inside the group at
        /// <paramref name="key"/>, making that group real: the rest of the run becomes its one seeded
        /// occupant — ridden into the boundary results here, no encoding holding it — and the ordinary
        /// descent takes over.
        /// </summary>
        /// <param name="plan"><inheritdoc cref="ResolveBoundaries" path="/param[@name='plan']"/></param>
        /// <param name="writer"><inheritdoc cref="ApplyGroup" path="/param[@name='writer']"/></param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private NodeResult ApplyChainSplit(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, in PbtNodeChain chain,
            scoped BucketPlan plan, ref BufferWriter writer, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            int childDepth = depth + PbtLayout.TrieNodeGroupLevelsPerGroup;
            Stem targetPath = chain.TargetPath;
            ValueHash256 targetHash = chain.TargetHash;

            // The run below this frame, at the one slot its path passes through: the target group itself
            // when it is the direct child, otherwise what is left of the run. Neither is stored under that
            // key — a boundary internal points at the target, and the remainder has never been a blob.
            int targetSlot = NibbleOf(targetPath, depth);
            bool directChild = childDepth == chain.TargetDepth;
            using Occupant seed = directChild
                ? NewInternalNode(targetHash)
                : NewChainNode(childDepth, chain.TargetDepth, targetPath, targetHash, chain.Stats);
            // a seeded run is the one occupant a frame can start with that is not a pointer to a blob
            uint occupantsOccupied = 1u << targetSlot;
            NodeGroupBitmasks occupantsShape = new(occupantsOccupied, Stems: 0, directChild ? 0 : occupantsOccupied);

            // The group this split makes real clusters its children where its depth says so, and the target
            // is one of them: its blob moves out of the key the run left it under and into the cluster
            // this frame is about to write. A run below the child depth is not stored anywhere to begin
            // with, and rides in the seed as it always has.
            using RefCountingMemory? adopted = directChild && PbtLayout.IsClusteringDepth(depth)
                ? store.GetTrieNode(chain.TargetKey)
                : null;
            if (adopted is not null) store.SetTrieNode(chain.TargetKey, null);

            // The run reached one subtree and nothing else, so it is the whole of what is here: resolve it
            // into a shared buffer and rebuild the group the split makes.
            SeededOccupant occupants = new(seed, targetSlot, StoredBlob.Of(adopted));
            RefList16<NodeResult> resultBuffer = new(PbtLayout.TrieNodeGroupBoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();

            PbtNodeCluster.Builder builder = default;
            int mark = writer.WrittenCount;

            GroupShape shape = ResolveBoundaries(key, entries, occupants, plan, results, ref writer, ref builder)
                .MergeUntouched(occupantsShape);
            // The seeded run is held by no encoding, so nothing can read it back later: it rides on in
            // `results` unless the descent already refreshed its slot.
            if ((shape.TouchedBitmask >> targetSlot & 1) == 0)
            {
                NodeResult seeded = seed;

                // only a run's result keeps hold of the memory, so only it takes a lease of its own — a
                // boundary internal promotes to a pure value, leaving the seed's lease with the seed
                results[targetSlot] = seeded.Lease();
            }

            return RebuildNode(
                key, occupants, default, results, shape, chain.NodeHash, chain.Stats, ref writer, ref builder, mark,
                out changed, out delta);
        }

        /// <summary>
        /// Folds <paramref name="inner"/> — the node now at <paramref name="innerKey"/> — back into a run
        /// starting at <paramref name="startDepth"/>, taking <paramref name="inner"/>'s lease.
        /// </summary>
        /// <remarks>
        /// An internal node roots a run reaching down to it. A run of its own extends this one instead,
        /// which is what keeps runs maximal (invariant 2) and what leaves <paramref name="innerKey"/> a key
        /// nothing may hold any more — removed here when <paramref name="innerBlobStored"/> says a blob was
        /// read there, which is a group that has since collapsed into the run. A stem or an empty subtree
        /// dissolves the run: a stem's node hash does not depend on where it sits, so it hoists on up
        /// untouched.
        /// </remarks>
        /// <param name="stats">
        /// What <paramref name="inner"/>'s subtree amounts to, which the run it becomes reaches and
        /// nothing else.
        /// </param>
        private NodeResult WrapIntoChain(
            int startDepth, in TrieNodeKey innerKey, NodeResult inner, bool innerBlobStored, in PbtSubtreeStats stats)
        {
            switch (inner.Kind)
            {
                case NodeKind.Absent:
                case NodeKind.Stem:
                    if (innerBlobStored) store.SetTrieNode(innerKey, null);
                    return inner;
                case NodeKind.Internal:
                    // plant the rebuilt encoding when the descent produced one, else the stored blob stands
                    using (inner)
                    {
                        if (inner.Blob is { } innerBlob)
                        {
                            innerBlob.AcquireLease();
                            store.SetTrieNode(innerKey, innerBlob);
                        }
                        return NewChainNode(startDepth, innerKey.Depth, innerKey.Path, inner.Hash, stats);
                    }
                default:
                    PbtNodeChain absorbed = PbtNodeChain.Decode(inner.ChainData, innerKey.Depth);
                    int targetDepth = absorbed.TargetDepth;
                    Stem targetPath = absorbed.TargetPath;
                    ValueHash256 targetHash = absorbed.TargetHash;
                    Debug.Assert(absorbed.Stats == stats, "an absorbed run reaches the same subtree the run absorbing it does");
                    if (innerBlobStored) store.SetTrieNode(innerKey, null);
                    using (inner) return NewChainNode(startDepth, targetDepth, targetPath, targetHash, stats);
            }
        }
    }
}
