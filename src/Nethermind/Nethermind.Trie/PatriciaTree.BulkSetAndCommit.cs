// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
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

            using (ICommitter committer = new NonPruningCommitter(TrieStore.BeginCommit(RootRef, writeFlags)))
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

            using (ICommitter committer = new NonPruningCommitter(TrieStore.BeginCommit(RootRef, writeFlags)))
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

            using (ICommitter committer = new NonPruningCommitter(TrieStore.BeginCommit(RootRef, writeFlags)))
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
    /// Seals, hashes, and optionally persists the root. Called at end of BulkSetAndCommit entrypoints.
    /// At this point <paramref name="root"/> has been hashed (Keccak != null) by the recursion but is
    /// not yet sealed. Caller is responsible for setting RootRef and SetRootHash after committer disposes.
    /// </summary>
    private void FinalizeRoot(ICommitter committer, ref TreePath path, TrieNode? root, bool skipRoot)
    {
        if (root is null || !root.IsDirty)
            return;

        root.Seal();
        if (!skipRoot && root.FullRlp.Length >= 32)
            committer.CommitNode(ref path, root);
    }

    /// <summary>
    /// Fused recursive worker.
    ///
    /// Invariant on return: all strict descendants of the returned node have been committed;
    /// the returned node itself has its Keccak computed (ResolveKey called) but is NOT yet
    /// sealed and NOT yet committed. The caller (or the public entrypoint) seals and commits it.
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
            // CommitNode is called concurrently inside workers (for deep descendants).
            // The 16 subtree roots are NOT committed here; they are committed serially below
            // by the main thread after the join.
            // Wrap the committer to serialize concurrent CommitNode calls, since IWriteBatch
            // implementations (e.g. InMemoryWriteBatch) are not guaranteed to be thread-safe.
            ICommitter closureCommitter = new SynchronizedCommitter(committer);
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

            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? child = jobs[i].currentChild;
                TrieNode? newChild = jobs[i].newChild;

                // BulkSetAndCommit computes Keccak on returned nodes, so we cannot rely solely on
                // ShouldUpdateChild's "Keccak is null" heuristic to detect in-place mutations.
                // Also propagate when newChild is dirty (was created/mutated in this operation).
                if (!ShouldUpdateChild(originalNode, child, newChild) && (newChild is null || !newChild.IsDirty)) continue;

                if (newChild is null)
                    hasRemove = true;

                if (newChild is not null)
                    nonNullChildCount++;

                if (node.IsSealed)
                    node = node.Clone();

                node.SetChild(i, newChild);
            }
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

                if (!ShouldUpdateChild(originalNode, child, newChild) && (newChild is null || !newChild.IsDirty))
                    continue;

                if (newChild is null)
                    hasRemove = true;

                if (newChild is not null)
                    nonNullChildCount++;

                if (node.IsSealed)
                    node = node.Clone();

                node.SetChild(nib, newChild);
            }

            path.TruncateOne();
        }

        if (!hasRemove && nonNullChildCount == 0) return originalNode;

        if ((hasRemove || newBranch) && nonNullChildCount < 2)
            node = MaybeCombineNode(ref path, node, originalNode);

        // Commit surviving dirty children (those from our recursion, identified by IsDirty).
        // Pre-existing committed nodes are sealed (IsDirty == false) and skipped by TryGetDirtyChild.
        // Nodes dropped by MaybeCombineNode are no longer reachable — never committed. No orphan writes.
        // Note: call ResolveKey before Seal — MakeFakeBranch children may not have Keccak computed yet.
        if (node is not null && node.IsDirty)
        {
            if (node.IsBranch)
            {
                path.AppendMut(0);
                for (int i = 0; i < 16; i++)
                {
                    if (node.TryGetDirtyChild(i, out TrieNode? dirtyChild))
                    {
                        path.SetLast(i);
                        dirtyChild.ResolveKey(TrieStore, ref path, _bufferPool, canBeParallel: false);
                        dirtyChild.Seal();
                        if (dirtyChild.FullRlp.Length >= 32)
                        {
                            TrieNode committed = committer.CommitNode(ref path, dirtyChild);
                            if (!ReferenceEquals(dirtyChild, committed))
                                node[i] = committed;
                        }
                    }
                }
                path.TruncateOne();
            }
            else if (node.NodeType == NodeType.Extension)
            {
                int prev = node.AppendChildPath(ref path, 0);
                if (node.TryGetDirtyChild(0, out TrieNode? dirtyChild))
                {
                    dirtyChild.ResolveKey(TrieStore, ref path, _bufferPool, canBeParallel: false);
                    dirtyChild.Seal();
                    if (dirtyChild.FullRlp.Length >= 32)
                    {
                        TrieNode committed = committer.CommitNode(ref path, dirtyChild);
                        if (!ReferenceEquals(dirtyChild, committed))
                            node[0] = committed;
                    }
                }
                path.TruncateMut(prev);
            }
            // Leaf: no children to commit.

            // Hash self — caller seals and commits (invariant C).
            node.ResolveKey(TrieStore, ref path, _bufferPool, canBeParallel: path.Length == 0);
        }

        return node;
    }

    /// <summary>
    /// Walks a dirty subtree (e.g. from BulkSetOne / SetNew, or a pre-existing dirty root)
    /// and commits all descendants bottom-up; hashes the top-level <paramref name="node"/> but
    /// does NOT seal it. The caller seals and commits <paramref name="node"/>.
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
        // Leaf: terminal, no children.

        // Hash self; caller seals and commits.
        node.ResolveKey(TrieStore, ref path, _bufferPool, canBeParallel: path.Length == 0);
    }

    private sealed class SynchronizedCommitter(ICommitter inner) : ICommitter
    {
        public void Dispose() => inner.Dispose();

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            lock (inner)
                return inner.CommitNode(ref path, node);
        }
    }

    // Prevents pruning (TrieNode → Hash256 replacement) of nodes committed mid-recursion.
    // Nodes are written to the in-flight write batch but the batch is not flushed until Dispose,
    // so setting IsPersisted=true would cause GetChildWithChildPath to prune references that
    // cannot yet be reloaded from the backing store.
    private sealed class NonPruningCommitter(ICommitter inner) : ICommitter
    {
        public void Dispose() => inner.Dispose();

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            TrieNode result = inner.CommitNode(ref path, node);
            result.IsPersisted = false;
            return result;
        }
    }
}
