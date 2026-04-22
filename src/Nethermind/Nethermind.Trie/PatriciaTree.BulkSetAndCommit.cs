// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    /// <summary>
    /// Fused BulkSet + Commit in a single descent: sets all <paramref name="entries"/>, then
    /// hashes and persists every touched node bottom-up on the return path — no second descent.
    /// Semantics are identical to <c>BulkSet(entries, flags); Commit(skipRoot, writeFlags);</c>.
    /// </summary>
    public void BulkSetAndCommit(
        in ArrayPoolListRef<BulkSetEntry> entries,
        Flags flags = Flags.None,
        bool skipRoot = false,
        WriteFlags writeFlags = WriteFlags.None)
    {
        if (!_allowCommits)
            ThrowReadOnlyTrieException();

#if ZK_EVM
        flags |= Flags.DoNotParallelize;
#endif

        // Empty-entry path: still commit any pre-existing dirty root.
        if (entries.Count == 0)
        {
            if (RootRef is null || !RootRef.IsDirty)
                return;

            using (ICommitter committer = TrieStore.BeginCommit(RootRef, writeFlags))
            {
                TreePath emptyPath = TreePath.Empty;
                HashAndCommitDescendants(committer, ref emptyPath, RootRef, null);
                FinalizeRoot(committer, ref emptyPath, RootRef, skipRoot);
            }
            SetRootHash(RootRef?.Keccak, true);
            return;
        }

        TraverseStack traverseStack = _threadStaticTraverseStack ?? new();
        _threadStaticTraverseStack = null;

        TreePath path = TreePath.Empty;
        TrieNode? newRoot;

        if (entries.Count < InPlaceSortThreshold)
        {
            Context ctx = new()
            {
                OriginalEntriesArray = entries.UnsafeGetInternalArray(),
                OriginalSortBufferArray = entries.UnsafeGetInternalArray(),
            };

            using (ICommitter committer = TrieStore.BeginCommit(RootRef, writeFlags))
            {
                newRoot = BulkSetAndCommit(ctx, traverseStack, committer,
                    entries.AsSpan(), entries.AsSpan(),
                    ref path, RootRef, 0, flags);
                FinalizeRoot(committer, ref path, newRoot, skipRoot);
            }
        }
        else
        {
            using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(entries.Count, entries.Count);
            Context ctx = new()
            {
                OriginalSortBufferArray = sortBuffer.UnsafeGetInternalArray(),
                OriginalEntriesArray = entries.UnsafeGetInternalArray(),
            };

            using (ICommitter committer = TrieStore.BeginCommit(RootRef, writeFlags))
            {
                newRoot = BulkSetAndCommit(ctx, traverseStack, committer,
                    entries.AsSpan(), sortBuffer.AsSpan(),
                    ref path, RootRef, 0, flags);
                FinalizeRoot(committer, ref path, newRoot, skipRoot);
            }
        }

        // _writeBeforeCommit intentionally NOT updated: fused op leaves no pending dirty nodes.
        _threadStaticTraverseStack = traverseStack;
        RootRef = newRoot;
        SetRootHash(RootRef?.Keccak, true);
    }

    /// <summary>
    /// Hashes, seals, and optionally persists the root. Called at end of BulkSetAndCommit entrypoints.
    /// The recursion deliberately leaves <paramref name="root"/> un-hashed and un-sealed — Keccak is
    /// computed here, right before Seal+CommitNode, so we don't hash intermediate nodes that
    /// MaybeCombineNode might later discard. Caller is responsible for setting RootRef and
    /// SetRootHash after the committer disposes.
    /// </summary>
    private void FinalizeRoot(ICommitter committer, ref TreePath path, TrieNode? root, bool skipRoot)
    {
        if (root is null || !root.IsDirty)
            return;

        root.ResolveKey(TrieStore, ref path, _bufferPool, canBeParallel: path.Length == 0);
        root.Seal();
        if (!skipRoot && root.FullRlp.Length >= 32)
            committer.CommitNode(ref path, root);
    }

    /// <summary>
    /// Fused recursive worker.
    ///
    /// Invariant on return: all strict descendants of the returned node have been committed.
    /// The returned node itself is left un-hashed, un-sealed, and un-committed — the caller's
    /// commit loop (or FinalizeRoot) calls ResolveKey+Seal+CommitNode right before it's needed,
    /// so nodes absorbed by a parent's MaybeCombineNode never pay the hashing cost.
    /// </summary>
    private TrieNode? BulkSetAndCommit(
        in Context ctx,
        TraverseStack traverseStack,
        ICommitter committer,
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> sortBuffer,
        ref TreePath path,
        TrieNode? node,
        int flipCount,
        Flags flags)
    {
        TrieNode? originalNode = node;

        // Single-entry fast path: delegate to SetNew then commit the resulting chain.
        if (entries.Length == 1)
        {
            TrieNode? newSub = BulkSetOne(traverseStack, in entries[0], ref path, node);
            HashAndCommitDescendants(committer, ref path, newSub, originalNode);
            return newSub;
        }

        bool newBranch = false;
        // MakeFakeBranch seeds the fake branch with a child at the original key's first
        // nibble — sometimes dirty (fresh leaf / fresh shortened extension), sometimes clean
        // (re-used child of a 1-nibble extension). The inline-commit state machine below
        // treats a dirty seed as the "first dirty child" so the main loop's actual first
        // dirty child becomes the second — triggering inline commits from that point.
        int makeFakeBranchNib = -1;

        if (node is null)
        {
            node = TrieNodeFactory.CreateBranch();
            newBranch = true;
        }
        else
        {
            node.ResolveNode(TrieStore, path);

            if (!node.IsBranch)
            {
                makeFakeBranchNib = node.Key![0];
                node = MakeFakeBranch(ref path, node);
                newBranch = true;
            }
        }

        Span<int> indexes = stackalloc int[TrieNode.BranchesCount];

        if (path.Length == 64)
            throw new InvalidOperationException("Non unique entry keys");

        int nibMask;

        if ((flags & Flags.WasSorted) != 0)
        {
            nibMask = HexarySearchAlreadySorted(entries, path.Length, indexes);
        }
        else if (entries.Length <= 3)
        {
            nibMask = SortTiny(entries, path.Length, indexes);
        }
        else if (entries.Length < InPlaceSortThreshold)
        {
            nibMask = InPlaceBucketSort16(entries, path.Length, indexes);
        }
        else
        {
            nibMask = BucketSort16(entries, sortBuffer, path.Length, indexes);
            flipCount++;

            Span<BulkSetEntry> newBufferSpan = entries;
            entries = sortBuffer;
            sortBuffer = newBufferSpan;
        }

        bool hasRemove = false;
        int nonNullChildCount = 0;

        // Inline-commit state machine.
        //
        // MaybeCombineNode can only transform a branch that has ≤ 1 non-null child, so as
        // soon as we've seen a second distinct dirty child every dirty child is safe to
        // commit immediately. Only the "first" dirty child has to stay buffered — and only
        // until MaybeCombineNode runs — because it may still be absorbed or discarded.
        //
        // pendingChild + pendingNib: the one candidate we hold back from inline commit.
        //   Pre-seeded from MakeFakeBranch's dirty seed so an untouched seed slot gets
        //   committed through the same post-flush path as a main-loop first dirty.
        TrieNode? pendingChild = null;
        int pendingNib = -1;

        if (makeFakeBranchNib >= 0 && node.TryGetDirtyChild(makeFakeBranchNib, out TrieNode? seed))
        {
            pendingChild = seed;
            pendingNib = makeFakeBranchNib;
        }

        // Inline commit of a dirty child already placed at node[slot]. Precondition:
        // caller has verified dc is the dirty child at slot and path points to it.
        void CommitDirtyAt(int slot, TrieNode dc, ref TreePath p)
        {
            dc.ResolveKey(TrieStore, ref p, _bufferPool, canBeParallel: false);
            dc.Seal();
            if (dc.FullRlp.Length >= 32)
            {
                TrieNode committed = committer.CommitNode(ref p, dc);
                if (!ReferenceEquals(dc, committed))
                    node[slot] = committed;
            }
        }

        // Per-slot decision, called after each SetChild in the main loop. `p` must point
        // at slot `slot` (parent path + slot nibble). pendingChild stays buffered across
        // the whole loop — no cross-slot flush — so the post-flush below commits exactly
        // one dirty child if anything remains.
        void MaybeCommitAt(ref TreePath p, int slot, TrieNode? newChild)
        {
            if (newChild is null || !newChild.IsDirty)
            {
                // SetChild just replaced the slot with null or a clean reference; any
                // previously-buffered dirty reference at this slot is now stale.
                if (pendingChild is not null && pendingNib == slot) pendingChild = null;
                return;
            }

            if (pendingChild is null)
            {
                pendingChild = newChild;
                pendingNib = slot;
            }
            else if (pendingNib == slot)
            {
                // Same slot rewritten — still one candidate, just update the reference.
                pendingChild = newChild;
            }
            else
            {
                // Second-or-later distinct dirty slot — safe to commit inline. The buffered
                // one stays, to be flushed after MaybeCombineNode.
                CommitDirtyAt(slot, newChild, ref p);
            }
        }

        if (entries.Length >= MinEntriesToParallelizeThreshold && nibMask == FullBranch && !flags.HasFlag(Flags.DoNotParallelize))
        {
            using ArrayPoolList<(
                int startIdx,
                int count,
                int nibble,
                TreePath appendedPath,
                TrieNode? currentChild,
                TrieNode? newChild
                )> jobs = new(TrieNode.BranchesCount, TrieNode.BranchesCount);

            Context closureCtx = ctx;
            BulkSetEntry[] originalEntriesArray = (flipCount % 2 == 0) ? ctx.OriginalEntriesArray : ctx.OriginalSortBufferArray;
            BulkSetEntry[] originalBufferArray = (flipCount % 2 == 0) ? ctx.OriginalSortBufferArray : ctx.OriginalEntriesArray;
            TrieNode.ChildIterator childIterator = node.CreateChildIterator();

            while (nibMask != 0)
            {
                int nib = BitOperations.TrailingZeroCount(nibMask);
                nibMask &= nibMask - 1;
                int startRange = indexes[nib];
                int endRange = nibMask != 0 ? indexes[BitOperations.TrailingZeroCount(nibMask)] : entries.Length;

                Span<BulkSetEntry> jobEntry = entries.Slice(startRange, endRange - startRange);
                TreePath childPath = path.Append(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(TrieStore, ref childPath, nib);
                jobs[nib] = (GetSpanOffset(originalEntriesArray, jobEntry), jobEntry.Length, nib, childPath, child, null);
            }

            // Workers call BulkSetAndCommit recursively and commit descendants concurrently.
            // CommitNode is called concurrently inside workers (for deep descendants); the 16 subtree
            // roots are NOT committed here, they are committed serially below after the join.
            // The supplied ICommitter MUST be concurrency-safe for CommitNode. Production TrieStore's
            // BlockCommitter is; tests that use a non-thread-safe committer must wrap it themselves.
            ICommitter closureCommitter = committer;
            Parallel.For(0, TrieNode.BranchesCount, ParallelUnbalancedWork.DefaultOptions,
                () =>
                {
                    TraverseStack s = _threadStaticTraverseStack ?? new();
                    _threadStaticTraverseStack = null;
                    return s;
                },
                (i, _, workerTraverseStack) =>
                {
                    (int startIdx, int count, int nib, TreePath childPath, TrieNode? child, TrieNode? _) = jobs[i];

                    Span<BulkSetEntry> jobEntries = originalEntriesArray.AsSpan(startIdx, count);
                    Span<BulkSetEntry> bufferEntries = originalBufferArray.AsSpan(startIdx, count);

                    TrieNode? newChild = BulkSetAndCommit(
                        in closureCtx,
                        workerTraverseStack,
                        closureCommitter,
                        jobEntries,
                        bufferEntries,
                        ref childPath,
                        child,
                        flipCount,
                        flags & ~Flags.DoNotParallelize);

                    jobs[i] = (startIdx, count, nib, childPath, child, newChild);
                    return workerTraverseStack;
                },
                s => _threadStaticTraverseStack = s
            );

            path.AppendMut(0);
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? child = jobs[i].currentChild;
                TrieNode? newChild = jobs[i].newChild;

                // Check IsDirty first — it's the cheap/fast filter: any new-or-mutated node is dirty.
                // ShouldUpdateChild's "Keccak is null" heuristic is insufficient because BulkSetAndCommit
                // resolves Keccak on returned nodes, so a mutated node can still compare "clean" by Keccak.
                if ((newChild is null || !newChild.IsDirty) && !ShouldUpdateChild(originalNode, child, newChild)) continue;

                if (newChild is null)
                    hasRemove = true;

                if (newChild is not null)
                    nonNullChildCount++;

                if (node.IsSealed)
                    node = node.Clone();

                node.SetChild(i, newChild);

                path.SetLast(i);
                MaybeCommitAt(ref path, i, newChild);
            }
            path.TruncateOne();
        }
        else
        {
            TrieNode.ChildIterator childIterator = node.CreateChildIterator();
            path.AppendMut(0);

            while (nibMask != 0)
            {
                int nib = BitOperations.TrailingZeroCount(nibMask);
                nibMask &= nibMask - 1;
                int startRange = indexes[nib];

                path.SetLast(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(TrieStore, ref path, nib);

                int endRange = nibMask != 0 ? indexes[BitOperations.TrailingZeroCount(nibMask)] : entries.Length;

                TrieNode? newChild;
                if (endRange - startRange == 1)
                {
                    newChild = BulkSetOne(traverseStack, entries[startRange], ref path, child);
                    HashAndCommitDescendants(committer, ref path, newChild, child);
                }
                else
                {
                    newChild = BulkSetAndCommit(in ctx, traverseStack, committer,
                        entries[startRange..endRange],
                        sortBuffer[startRange..endRange],
                        ref path, child, flipCount, flags);
                }

                if ((newChild is null || !newChild.IsDirty) && !ShouldUpdateChild(originalNode, child, newChild))
                    continue;

                if (newChild is null)
                    hasRemove = true;

                if (newChild is not null)
                    nonNullChildCount++;

                if (node.IsSealed)
                    node = node.Clone();

                node.SetChild(nib, newChild);

                MaybeCommitAt(ref path, nib, newChild);
            }

            path.TruncateOne();
        }

        if (!hasRemove && nonNullChildCount == 0) return originalNode;

        if ((hasRemove || newBranch) && nonNullChildCount < 2)
            node = MaybeCombineNode(ref path, node, originalNode);

        // Flush the buffered candidate.
        //
        // Branch — MaybeCombineNode didn't fold anything (either it didn't run, or it ran
        // and its scan found ≥ 2 children and returned node unchanged). pendingChild is the
        // sole uncommitted dirty child — commit it directly, no TryGetDirtyChild needed.
        //
        // Extension — MaybeCombineNode folded. Two sub-flavors to distinguish, and slot 0
        // differs between them:
        //   (a) Branch-only fold: pendingChild was a Branch, the surrounding Branch was
        //       wrapped in a single-nibble Extension around it (Cases D, E). Slot 0 IS
        //       pendingChild and is still dirty — commit.
        //   (b) Branch + child fold: pendingChild was itself an Extension, absorbed into
        //       a longer-key Extension (originalNode key-match Case F2, or the fallback
        //       CloneWithChangedKey in Case H-extension). Slot 0 is pendingChild's own
        //       child (a committed descendant) — must NOT commit.
        //   Plus: originalNode-returned-unchanged (Cases C, F1) with slot 0 = original's
        //       pre-existing clean child — must NOT commit.
        // TryGetDirtyChild(0) returns true only in (a), false in the rest.
        //
        // Leaf / null — pendingChild was absorbed into a leaf clone (Case H-leaf / Case G)
        // or the whole branch became empty — no commit.
        if (pendingChild is not null && node is not null && node.IsDirty)
        {
            if (node.IsBranch)
            {
                path.AppendMut(pendingNib);
                CommitDirtyAt(pendingNib, pendingChild, ref path);
                path.TruncateOne();
            }
            else if (node.NodeType == NodeType.Extension)
            {
                int prev = node.AppendChildPath(ref path, 0);
                if (node.TryGetDirtyChild(0, out TrieNode? dc))
                    CommitDirtyAt(0, dc, ref path);
                path.TruncateMut(prev);
            }
        }

        // Leave node un-hashed: caller's commit loop (or FinalizeRoot) calls ResolveKey immediately
        // before sealing/committing, so if node gets absorbed by the caller's MaybeCombineNode the
        // Keccak cost is avoided.
        return node;
    }

    /// <summary>
    /// Walks a dirty subtree (e.g. from BulkSetOne / SetNew, or a pre-existing dirty root) and
    /// commits every descendant bottom-up. Leaves <paramref name="node"/> itself un-hashed and
    /// un-sealed — the caller calls ResolveKey right before Seal+CommitNode, so nodes absorbed
    /// by a parent's MaybeCombineNode never pay the hashing cost.
    /// </summary>
    private void HashAndCommitDescendants(
        ICommitter committer, ref TreePath path,
        TrieNode? node, TrieNode? originalNode)
    {
        if (node is null || ReferenceEquals(node, originalNode) || !node.IsDirty)
            return;

        if (node.IsBranch)
        {
            path.AppendMut(0);
            for (int i = 0; i < 16; i++)
            {
                if (node.TryGetDirtyChild(i, out TrieNode? child))
                {
                    path.SetLast(i);
                    HashAndCommitDescendants(committer, ref path, child, null);
                    child.ResolveKey(TrieStore, ref path, _bufferPool, canBeParallel: false);
                    child.Seal();
                    if (child.FullRlp.Length >= 32)
                    {
                        TrieNode committed = committer.CommitNode(ref path, child);
                        if (!ReferenceEquals(child, committed))
                            node[i] = committed;
                    }
                }
            }
            path.TruncateOne();
        }
        else if (node.NodeType == NodeType.Extension)
        {
            int prev = node.AppendChildPath(ref path, 0);
            if (node.TryGetDirtyChild(0, out TrieNode? child))
            {
                HashAndCommitDescendants(committer, ref path, child, null);
                child.ResolveKey(TrieStore, ref path, _bufferPool, canBeParallel: false);
                child.Seal();
                if (child.FullRlp.Length >= 32)
                {
                    TrieNode committed = committer.CommitNode(ref path, child);
                    if (!ReferenceEquals(child, committed))
                        node[0] = committed;
                }
            }
            path.TruncateMut(prev);
        }
        // Leaf: terminal, no children. Caller hashes/seals/commits.
    }

}
