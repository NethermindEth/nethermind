// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    /// <summary>One top nibble's job in <see cref="BulkSetAndHashByTopNibble"/>.</summary>
    private record struct FusedJob(int Nibble, int Start, int Count, TrieNode? Child, TrieNode? NewChild, bool Update);

    /// <summary>
    /// Bench (B'): whether <see cref="BulkSetAndHashByTopNibble"/> may run on this tree now.
    /// </summary>
    /// <remarks>
    /// It hashes in the jobs, the way <see cref="UpdateRootHash"/> does after a bulk set, which needs AVX2 and more than
    /// one core; a build that never parallelizes a bulk set never fuses one either. The root must be absent or sealed:
    /// then every dirty node after the write is one this write created, so the hashing in the jobs sets the keccak only
    /// of new nodes, and the decisions taken on this thread afterwards, which compare a child with the one it replaces
    /// and read its keccak when they are the same node (<see cref="ShouldUpdateChild"/>), come out as without it. A
    /// dirty root, left by an earlier write of the same block, can be changed in place below; that write takes
    /// <see cref="BulkSet"/>.
    /// </remarks>
    public bool CanFuseBulkSetAndHash
    {
        get
        {
            Flags flags = Flags.None;
            AdjustBulkSetFlags(ref flags);
            return Avx2.IsSupported && !Core.Cpu.RuntimeInformation.IsSingleProcessor
                && (flags & Flags.DoNotParallelize) == 0 && RootRef is not { IsDirty: true };
        }
    }

    /// <summary>
    /// Bench (B'): <see cref="BulkSet"/> with the hashing of <see cref="UpdateRootHash"/> below the root fused in. One job
    /// per top nibble that has entries fills in their values, writes them into its child of the root and hashes that
    /// child's subtree at once, while its nodes are in that core's cache; the root is then rebuilt on this thread as
    /// <see cref="BulkSet"/> rebuilds it. <see cref="UpdateRootHash"/> is still called afterwards and is left the root.
    /// </summary>
    /// <remarks>
    /// The children own disjoint subtrees and the digests do not depend on the order they are taken in, which
    /// <see cref="DirtyNodeHasher"/> already relies on. A child left unhashed, by the root's collapse or by a subtree
    /// too small for the level order, is hashed by <see cref="UpdateRootHash"/> as it would have been anyway.
    /// </remarks>
    /// <param name="entries">The entries grouped by top nibble: nibble n's at [<paramref name="nibbleStarts"/>[n],
    /// <paramref name="nibbleStarts"/>[n + 1]). Their paths must be unique.</param>
    /// <param name="nibbleStarts">Seventeen offsets into <paramref name="entries"/>; the last is the entry count.</param>
    /// <param name="prepare">Called by a nibble's job with the start and length of its range before it writes the range,
    /// to fill in the values; <see langword="null"/> when they are already there.</param>
    public void BulkSetAndHashByTopNibble(BulkSetEntry[] entries, ReadOnlySpan<int> nibbleStarts, Action<int, int>? prepare)
    {
        if (!CanFuseBulkSetAndHash) throw new InvalidOperationException("A fused bulk set needs AVX2, more than one core and a sealed or absent root.");

        int count = nibbleStarts[TrieNode.BranchesCount];
        if (count == 0) return;

        // Bench-only: the fused loop and its work items, run by the block-processing thread or by helpers.
        BlockDiag? diag = BlockDiag.InPhase(BlockDiag.Phase.AccountTrie);
        diag?.Add(BlockDiag.C.FusedLoops, 1);

        TreePath path = TreePath.Empty;
        TrieNode? originalNode = RootRef;
        TrieNode node;
        bool newBranch = false;

        // As BulkSet opens the root.
        if (originalNode is null)
        {
            node = TrieNodeFactory.CreateBranch();
            newBranch = true;
        }
        else
        {
            originalNode.ResolveNode(TrieStore, path);
            node = originalNode;
            if (!node.IsBranch)
            {
                node = MakeFakeBranch(ref path, node);
                newBranch = true;
            }
        }

        using ArrayPoolList<FusedJob> jobs = new(TrieNode.BranchesCount);
        TrieNode.ChildIterator childIterator = node.CreateChildIterator();
        for (int nib = 0; nib < TrieNode.BranchesCount; nib++)
        {
            int start = nibbleStarts[nib];
            int length = nibbleStarts[nib + 1] - start;
            if (length == 0) continue;

            TreePath childPath = path.Append(nib);
            TrieNode? child = childIterator.GetChildWithChildPath(TrieStore, ref childPath, nib);
            jobs.Add(new FusedJob(nib, start, length, child, null, false));
        }

        // Entries stay in the array they came in, so a nested parallel bulk set finds its range at the same offset in both.
        using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(count, count);
        BulkSetEntry[] sortBufferArray = sortBuffer.UnsafeGetInternalArray();
        Context ctx = new()
        {
            OriginalEntriesArray = entries,
            OriginalSortBufferArray = sortBufferArray,
        };

        using (ParallelUnbalancedWork.BeginWorkerScope(Core.Cpu.RuntimeInformation.ProcessorCount))
        {
            // The account write and the hash below the root are one loop here.
            long loopStart = diag?.LoopStarted(BlockDiag.F.PlAccountFirst) ?? 0;
            ParallelUnbalancedWork.For(0, jobs.Count, ParallelUnbalancedWork.DefaultOptions,
                GetTraverseStack,
                (i, workerTraverseStack) =>
                {
                    diag?.CountWorkItem(BlockDiag.C.FusedMain, BlockDiag.C.FusedHelper, BlockDiag.F.PlAccountFirst, loopStart);
                    FusedJob job = jobs[i];
                    prepare?.Invoke(job.Start, job.Count);

                    TreePath childPath = TreePath.Empty;
                    childPath.AppendMut(job.Nibble);
                    TrieNode? newChild = BulkSet(
                        in ctx,
                        workerTraverseStack,
                        entries.AsSpan(job.Start, job.Count),
                        sortBufferArray.AsSpan(job.Start, job.Count),
                        ref childPath,
                        job.Child,
                        0,
                        Flags.None);

                    // Decided before the hashing below sets the keccak it reads, as BulkSet decides it.
                    bool update = ShouldUpdateChild(originalNode, job.Child, newChild);
                    if (newChild is not null && newChild.IsDirty && newChild.Keccak is null)
                    {
                        DirtyNodeHasher.HashRootChildSubtree(newChild, job.Nibble, TrieStore, _bufferPool);
                    }

                    jobs[i] = job with { NewChild = newChild, Update = update };
                    return workerTraverseStack;
                },
                ReturnTraverseStack);
        }

        // As BulkSet rebuilds the root from its parallel jobs.
        bool hasRemove = false;
        int nonNullChildCount = 0;
        foreach (FusedJob job in jobs.AsSpan())
        {
            if (!job.Update) continue;

            if (job.NewChild is null)
                hasRemove = true;
            else
                nonNullChildCount++;

            if (node.IsSealed)
                node = node.Unseal();

            node.SetChild(job.Nibble, job.NewChild);
        }

        TrieNode? newRoot;
        if (!hasRemove && nonNullChildCount == 0)
            newRoot = originalNode;
        else if ((hasRemove || newBranch) && nonNullChildCount < 2)
            newRoot = MaybeCombineNode(ref path, node, originalNode);
        else
            newRoot = node;

        RootRef = newRoot;
        _writeBeforeCommit += count;
    }
}
