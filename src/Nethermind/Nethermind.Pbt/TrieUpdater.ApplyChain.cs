// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private sealed partial class Updater
    {
        /// <summary>
        /// Builds the run of single-child levels from <paramref name="startDepth"/> down to the group at
        /// <paramref name="targetDepth"/>, whose subtree amounts to <paramref name="stats"/>, in memory of
        /// its own: a run is part of no group's encoding, and no blob holds its bytes until an ancestor
        /// plants them.
        /// </summary>
        private Occupant NewChainNode(
            int startDepth, int targetDepth, in Stem targetPath, in ValueHash256 targetHash, in PbtSubtreeStats stats)
        {
            RefCountingMemory memory = memoryProvider.Rent(PbtNodeChain.EncodedLength);
            PbtNodeChain.Write(memory.GetSpan(), startDepth, targetDepth, targetPath, targetHash, stats);
            return new Occupant(new NodeRef(ResultKind.Chain, 0), memory);
        }

        /// <summary>
        /// Copies an unchanged run's own encoding into memory of its own, for an ancestor to plant. The
        /// writes left its target group byte-identical, so the run — target hash, path, depths, stats and
        /// the node hash folded from them all unmoved — encodes to the very same bytes: they are handed up
        /// verbatim rather than spending the <see cref="PbtNodeChain.Fold"/> <see cref="NewChainNode"/>
        /// would to reproduce the cached node hash.
        /// </summary>
        private Occupant CopyChainNode(ReadOnlySpan<byte> chainData)
        {
            Debug.Assert(chainData.Length == PbtNodeChain.EncodedLength);
            RefCountingMemory memory = memoryProvider.Rent(PbtNodeChain.EncodedLength);
            chainData.CopyTo(memory.GetSpan());
            return new Occupant(new NodeRef(ResultKind.Chain, 0), memory);
        }

        /// <summary>
        /// Builds the boundary internal caching <paramref name="hash"/> in memory of its own, for a
        /// pointer to a stored group that no group's encoding holds yet.
        /// </summary>
        private Occupant NewInternalNode(in ValueHash256 hash)
        {
            RefCountingMemory memory = memoryProvider.Rent(PbtTrieNodeGroup.Slot.InternalLength);
            hash.Bytes.CopyTo(memory.GetSpan());
            return new Occupant(new NodeRef(ResultKind.Internal, 0), memory);
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to the run of single-child levels at <paramref name="key"/>,
        /// whose content is <paramref name="chainData"/> — a stored <see cref="PbtNodeChain"/> blob, or the
        /// rest of one an enclosing frame is descending.
        /// </summary>
        /// <remarks>
        /// The run holds no node between its start and its target, so nothing here walks it level by
        /// level: the entries' shallowest departure from its path says where the trie next branches, and
        /// the descent resumes at the group holding that bit — or at the target itself, when they all pass
        /// through. That inner frame partitions for itself: a precalculated bucket level describes one range
        /// at one depth, and skipping levels is exactly what leaves this frame's table describing neither, so
        /// the levels skipped over give up no precalculated bounds — a run only ever reaches past the topmost
        /// levels a producer buckets.
        /// </remarks>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ResolveBoundaries" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="result"><inheritdoc cref="ApplyStored" path="/param[@name='result']"/></param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private void ApplyChain(
            in TrieNodeKey key, Span<PbtWriteBatch.StemEntry> entries, ReadOnlySpan<byte> chainData,
            ReadOnlySpan<int> precalculatedBuckets, ref NodeResult result, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            Debug.Assert(!entries.IsEmpty);

            PbtNodeChain chain = PbtNodeChain.Decode(chainData, depth);
            int targetDepth = chain.TargetDepth;
            Stem targetPath = chain.TargetPath;

            // The shallowest bit at which a write leaves the run. FirstDifferingBit reports -1 for a stem
            // that never parts from the path, which as an unsigned compare reads as past everything —
            // which is what never parting means: the write descends the whole run.
            int splitDepth = targetDepth;
            for (int i = 0; i < entries.Length; i++)
            {
                int diff = entries[i].Stem.FirstDifferingBit(targetPath, depth);
                if ((uint)diff < (uint)splitDepth) splitDepth = diff;
            }

            Debug.Assert(splitDepth >= depth, "the entries share the run's path down to this frame");

            // The writes part from the run nowhere, so they descend the whole of it into its target group —
            // the one blob a run points at (invariant 2). Apply there and fold the result back into a run
            // starting here.
            if (splitDepth == targetDepth)
            {
                using RefCountingMemory? targetData = store.GetTrieNode(chain.TargetKey);
                NodeResult inner = default;
                ApplyStored(chain.TargetKey, entries, targetData, chain.TargetHash, default, ref inner, out bool targetChanged, out delta);

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
            int branchDepth = splitDepth & ~(PbtTrieNodeGroup.LevelsPerGroup - 1);
            bool branchesHere = branchDepth == depth;
            result = ApplyChainSplit(
                branchesHere ? key : TrieNodeKey.For(branchDepth, targetPath), depth, entries, chain,
                branchesHere ? precalculatedBuckets : default,
                out changed, out delta);
        }

        /// <summary>
        /// Applies <paramref name="entries"/> to a run that branches inside the group at
        /// <paramref name="key"/>, making that group real: the rest of the run becomes its one seeded
        /// occupant — ridden into the boundary results here, no encoding holding it — and the ordinary
        /// descent takes over. When the group sits below
        /// <paramref name="prefixDepth"/>, the split node is folded back into the run prefix reaching down
        /// to it.
        /// </summary>
        /// <param name="prefixDepth">
        /// The depth the enclosing run starts at, at or above <paramref name="key"/>'s. Equal to it when the
        /// branch is at the run's own frame — no prefix to wrap; shallower when the branch is in a deeper
        /// group, whose split node the run prefix from there down folds back into a run.
        /// </param>
        /// <param name="precalculatedBuckets"><inheritdoc cref="ResolveBoundaries" path="/param[@name='precalculatedBuckets']"/></param>
        /// <param name="changed"><inheritdoc cref="RebuildNode" path="/param[@name='changed']"/></param>
        /// <param name="delta"><inheritdoc cref="RebuildNode" path="/param[@name='delta']"/></param>
        private NodeResult ApplyChainSplit(
            in TrieNodeKey key, int prefixDepth, Span<PbtWriteBatch.StemEntry> entries, in PbtNodeChain chain,
            ReadOnlySpan<int> precalculatedBuckets, out bool changed, out PbtSubtreeStats delta)
        {
            int depth = key.Depth;
            int childDepth = depth + PbtTrieNodeGroup.LevelsPerGroup;
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
            uint occupantsOccupied = 1u << targetSlot;

            // The run reached one subtree and nothing else, so it is the whole of what is here: resolve it
            // into a shared buffer and rebuild the group the split makes.
            SeededOccupant occupants = new(seed, targetSlot);
            RefList16<NodeResult> resultBuffer = new(PbtTrieNodeGroup.BoundarySlots);
            Span<NodeResult> results = resultBuffer.AsSpan();

            GroupShape shape = ResolveBoundaries(key, entries, occupants, precalculatedBuckets, results)
                .MergeUntouched(occupantsOccupied, untouchedStems: 0);
            // The seeded run is held by no key and no encoding, so nothing can read it back later: it
            // rides on in `results` unless the descent already refreshed its slot.
            if ((shape.TouchedMask >> targetSlot & 1) == 0)
            {
                NodeResult seeded = seed;

                // only a run's result keeps hold of the memory, so only it takes a lease of its own — a
                // boundary internal promotes to a pure value, leaving the seed's lease with the seed
                results[targetSlot] = seeded.Lease();
            }

            NodeResult split = RebuildNode(key, occupants, default, results, shape, chain.NodeHash, chain.Stats, out changed, out delta);
            if (prefixDepth == depth) return split;

            // The branch fell in a deeper group than the run's start, so the split node hangs below a run
            // prefix reaching down to it, which holds no node of its own (invariant 3). That prefixed run's
            // hash — not the split group's — is what changed against the run this frame replaced.
            NodeResult result = WrapIntoChain(prefixDepth, key, split, innerBlobStored: false, chain.Stats + delta);
            changed = result.NodeHash() != chain.NodeHash;
            return result;
        }

        /// <summary>
        /// Folds <paramref name="inner"/> — the node now at <paramref name="innerKey"/> — back into a run
        /// starting at <paramref name="startDepth"/>, taking <paramref name="inner"/>'s lease.
        /// </summary>
        /// <remarks>
        /// An internal node roots a run reaching down to it. A run of its own extends this one instead,
        /// which is what keeps runs maximal (invariant 2) and what leaves <paramref name="innerKey"/> a key
        /// nothing may hold any more — removed here when <paramref name="innerBlobStored"/> says a blob was
        /// read there. A stem or an empty subtree dissolves the run: a stem's node hash does not depend on
        /// where it sits, so it hoists on up untouched.
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
                case ResultKind.Absent:
                case ResultKind.Stem:
                    // the run dissolved: the stem (or nothing) hoists on up, and innerKey — holding no
                    // group any more — is removed if it held one
                    if (innerBlobStored) store.SetTrieNode(innerKey, null);
                    return inner;
                case ResultKind.Internal:
                    // the run reaches down to the group at innerKey, which stays there; plant its rebuilt
                    // encoding when the descent produced one, else the stored blob stands
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
