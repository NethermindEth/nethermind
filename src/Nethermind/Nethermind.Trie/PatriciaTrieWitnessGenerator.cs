// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Collects the stateless witness for a single trie: given the set of read and written/deleted key paths, it
/// walks the pre-state trie once and reports every node a stateless verifier needs to re-execute the reads and
/// recompute the post-state root.
/// </summary>
/// <remarks>
/// The witness must cover every node that <em>any</em> apply order could touch, so a recursion returns <c>true</c>
/// ("treat this subtree as deleted", driving the parent's lone-child check) whenever <em>some</em> permutation of
/// the entries could empty the subtree — not only when the net result deletes it.
/// </remarks>
public static class PatriciaTrieWitnessGenerator
{
    private const int InPlaceSortThreshold = 32;
    private const int MinEntriesToParallelizeThreshold = 128;
    private const int FullBranch = (1 << TrieNode.BranchesCount) - 1;

    // MaybeEmptied arms the parent's collapse-sibling capture; Survived/Untraversed cannot be emptied under any order.
    private const byte Untraversed = 0;
    private const byte Survived = 1;
    private const byte MaybeEmptied = 2;

    // Deletion is encoded as a null entry value (BulkSet's "null == removal" convention); non-deleting accesses share this non-null sentinel.
    private static readonly byte[] NonDeleteMarker = [];

    /// <summary>How a key path was touched in this block.</summary>
    public enum AccessType : byte
    {
        /// <summary>The key was read or written without being removed; only its path is needed.</summary>
        Read,

        /// <summary>The key was removed.</summary>
        Delete,
    }

    /// <summary>A touched key path and how it was accessed in this block.</summary>
    public readonly struct PathEntry(in ValueHash256 path, AccessType access)
    {
        /// <summary>The full 64-nibble key path (account hash or storage-slot hash).</summary>
        public readonly ValueHash256 Path = path;

        /// <summary>How the key was touched.</summary>
        public readonly AccessType Access = access;
    }

    /// <summary>Receives the trie nodes that make up the witness as the generator walks a trie.</summary>
    /// <remarks>
    /// Only standalone nodes (those with their own <see cref="TrieNode.Keccak"/>) are reported, each exactly once.
    /// <see cref="Add"/> may be called concurrently and must be thread-safe when the generator runs in parallel.
    /// </remarks>
    public interface ISink
    {
        /// <summary>Reports a witness node at <paramref name="path"/>.</summary>
        void Add(in TreePath path, TrieNode node);
    }

    /// <summary>The two backing arrays the recursion flip-flops between; lets parallel workers recover their span.</summary>
    private readonly record struct Context(PatriciaTree.BulkSetEntry[] OriginalEntriesArray, PatriciaTree.BulkSetEntry[] OriginalSortBufferArray);

    /// <summary>
    /// Walks the trie at <paramref name="rootHash"/> and reports the witness nodes for <paramref name="paths"/> to
    /// <paramref name="sink"/>.
    /// </summary>
    /// <param name="sink">Receives the witness nodes. Must be thread-safe when <paramref name="parallelize"/> is set.</param>
    public static void Generate(
        ITrieNodeResolver resolver,
        Hash256 rootHash,
        ReadOnlySpan<PathEntry> paths,
        ISink sink,
        bool parallelize = false)
    {
        if (paths.Length == 0 || rootHash == Keccak.EmptyTreeHash) return;

        PatriciaTree.BulkSetEntry[] entriesArr = ArrayPool<PatriciaTree.BulkSetEntry>.Shared.Rent(paths.Length);
        // One root-sized buffer suffices for the whole walk: a deeper node never has more entries than the root.
        PatriciaTree.BulkSetEntry[]? bufferArr = paths.Length >= InPlaceSortThreshold
            ? ArrayPool<PatriciaTree.BulkSetEntry>.Shared.Rent(paths.Length)
            : null;
        try
        {
            for (int i = 0; i < paths.Length; i++)
            {
                byte[]? value = paths[i].Access == AccessType.Delete ? null : NonDeleteMarker;
                entriesArr[i] = new PatriciaTree.BulkSetEntry(paths[i].Path, value);
            }

            TreePath treePath = TreePath.Empty;
            TrieNode root = resolver.FindCachedOrUnknown(treePath, rootHash);

            Context ctx = new(entriesArr, bufferArr ?? entriesArr);
            Span<PatriciaTree.BulkSetEntry> entries = entriesArr.AsSpan(0, paths.Length);
            Span<PatriciaTree.BulkSetEntry> buffer = bufferArr is null ? entries : bufferArr.AsSpan(0, paths.Length);

            Walk(in ctx, resolver, root, ref treePath, entries, buffer, flipCount: 0, parallelize, sink);
        }
        finally
        {
            ArrayPool<PatriciaTree.BulkSetEntry>.Shared.Return(entriesArr);
            if (bufferArr is not null) ArrayPool<PatriciaTree.BulkSetEntry>.Shared.Return(bufferArr);
        }
    }

    /// <summary>
    /// The single recursive traversal (mirrors <c>PatriciaTree.BulkSet</c>). Reports every real node it visits and
    /// returns <c>true</c> iff some permutation of the entries could empty the subtree below <paramref name="node"/>.
    /// </summary>
    private static bool Walk(
        in Context ctx,
        ITrieNodeResolver resolver,
        TrieNode node,
        ref TreePath path,
        Span<PatriciaTree.BulkSetEntry> entries,
        Span<PatriciaTree.BulkSetEntry> sortBuffer,
        int flipCount,
        bool parallelize,
        ISink sink)
    {
        node.ResolveNode(resolver, path);
        // Inline nodes have no standalone hash; they live in their parent's already-reported RLP.
        if (node.Keccak is not null) sink.Add(path, node);

        if (node.IsLeaf || node.IsExtension)
        {
            return WalkKeyedNode(in ctx, resolver, node, ref path, entries, sortBuffer, flipCount, parallelize, sink);
        }

        // Bucketize by the nibble at this depth; the large path flips `entries`/`sortBuffer` (flipCount parity lets parallel workers recover the array).
        Span<int> indexes = stackalloc int[TrieNode.BranchesCount];
        int nibMask;
        if (entries.Length == 1)
        {
            int only = entries[0].GetPathNibble(path.Length);
            indexes[only] = 0;
            nibMask = 1 << only;
        }
        else if (entries.Length <= 3)
        {
            nibMask = PatriciaTree.SortTiny(entries, path.Length, indexes);
        }
        else if (entries.Length < InPlaceSortThreshold)
        {
            nibMask = PatriciaTree.InPlaceBucketSort16(entries, path.Length, indexes);
        }
        else
        {
            nibMask = PatriciaTree.BucketSort16(entries, sortBuffer, path.Length, indexes);
            flipCount++;
            Span<PatriciaTree.BulkSetEntry> sorted = sortBuffer;
            sortBuffer = entries;
            entries = sorted;
        }

        Span<byte> childState = stackalloc byte[TrieNode.BranchesCount];
        childState.Clear();

        if (entries.Length >= MinEntriesToParallelizeThreshold && nibMask == FullBranch && parallelize)
        {
            WalkBranchParallel(in ctx, resolver, node, in path, entries, indexes, flipCount, sink, childState);
        }
        else
        {
            TrieNode.ChildIterator childIterator = node.CreateChildIterator();
            path.AppendMut(0);
            int mask = nibMask;
            while (mask != 0)
            {
                int nib = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int start = indexes[nib];
                int end = mask != 0 ? indexes[BitOperations.TrailingZeroCount(mask)] : entries.Length;

                path.SetLast(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(resolver, ref path, nib);
                if (child is null) continue;

                bool childMaybeEmptied = Walk(in ctx, resolver, child, ref path, entries[start..end], sortBuffer[start..end], flipCount, parallelize, sink);
                childState[nib] = childMaybeEmptied ? MaybeEmptied : Survived;
            }
            path.TruncateOne();
        }

        return CollapseCheck(resolver, node, ref path, childState, sink);
    }

    /// <summary>
    /// Decides the "treat-as-deleted" answer for an already-reported keyed node (leaf or extension): <c>true</c> if any
    /// apply order could empty the subtree. Off-key entries cannot, so only deletions on this node's path matter.
    /// </summary>
    private static bool WalkKeyedNode(
        in Context ctx,
        ITrieNodeResolver resolver,
        TrieNode node,
        ref TreePath path,
        Span<PatriciaTree.BulkSetEntry> entries,
        Span<PatriciaTree.BulkSetEntry> sortBuffer,
        int flipCount,
        bool parallelize,
        ISink sink)
    {
        TreePath keyedPath = path;
        keyedPath.AppendMut(node.Key!);

        if (node.IsLeaf)
        {
            // An off-key insert cannot save the leaf: the delete may be applied first.
            ValueHash256 leafKey = keyedPath.Path;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Value is null && entries[i].Path == leafKey) return true;
            }
            return false;
        }

        // Extension: keep only entries within the prefix's subtree [lower, upper]; the rest branch off it and cannot empty its child.
        ValueHash256 lower = keyedPath.ToLowerBoundPath();
        ValueHash256 upper = keyedPath.ToUpperBoundPath();
        int m = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Path >= lower && entries[i].Path <= upper) entries[m++] = entries[i];
        }
        if (m == 0) return false;

        TrieNode? child = node.GetChildWithChildPath(resolver, ref keyedPath, 0);
        if (child is null) return false;
        return Walk(in ctx, resolver, child, ref keyedPath, entries[..m], sortBuffer[..m], flipCount, parallelize, sink);
    }

    /// <summary>Parallel form of the branch loop: each of the 16 disjoint child subtrees is walked on its own thread.</summary>
    private static void WalkBranchParallel(
        in Context ctx,
        ITrieNodeResolver resolver,
        TrieNode node,
        in TreePath path,
        Span<PatriciaTree.BulkSetEntry> entries,
        Span<int> indexes,
        int flipCount,
        ISink sink,
        Span<byte> childState)
    {
        // flipCount parity says which array `entries` currently lives in; recover both so each worker can rebuild its span from an offset (a Span cannot cross the Parallel.For boundary).
        PatriciaTree.BulkSetEntry[] originalEntries = (flipCount & 1) == 0 ? ctx.OriginalEntriesArray : ctx.OriginalSortBufferArray;
        PatriciaTree.BulkSetEntry[] originalBuffer = (flipCount & 1) == 0 ? ctx.OriginalSortBufferArray : ctx.OriginalEntriesArray;

        Job[] jobs = new Job[TrieNode.BranchesCount];
        TrieNode.ChildIterator childIterator = node.CreateChildIterator();
        int mask = FullBranch;
        while (mask != 0)
        {
            int nib = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int start = indexes[nib];
            int end = mask != 0 ? indexes[BitOperations.TrailingZeroCount(mask)] : entries.Length;
            Span<PatriciaTree.BulkSetEntry> jobEntries = entries[start..end];

            TreePath childPath = path.Append(nib);
            TrieNode? child = childIterator.GetChildWithChildPath(resolver, ref childPath, nib);
            jobs[nib] = new Job(GetSpanOffset(originalEntries, jobEntries), jobEntries.Length, childPath, child);
        }

        Context closureCtx = ctx;
        Parallel.For(0, TrieNode.BranchesCount, ParallelUnbalancedWork.DefaultOptions, i =>
        {
            TrieNode? child = jobs[i].Child;
            int count = jobs[i].Count;
            if (child is null || count == 0) return;

            Span<PatriciaTree.BulkSetEntry> e = originalEntries.AsSpan(jobs[i].Start, count);
            Span<PatriciaTree.BulkSetEntry> b = originalBuffer.AsSpan(jobs[i].Start, count);
            TreePath childPath = jobs[i].ChildPath;
            jobs[i].MaybeEmptied = Walk(in closureCtx, resolver, child, ref childPath, e, b, flipCount, parallelize: true, sink);
        });

        for (int nib = 0; nib < TrieNode.BranchesCount; nib++)
        {
            childState[nib] = jobs[nib].Child is not null && jobs[nib].Count > 0
                ? (jobs[nib].MaybeEmptied ? MaybeEmptied : Survived)
                : Untraversed;
        }
    }

    private struct Job(int start, int count, TreePath childPath, TrieNode? child)
    {
        public readonly int Start = start;
        public readonly int Count = count;
        public readonly TreePath ChildPath = childPath;
        public readonly TrieNode? Child = child;
        public bool MaybeEmptied;
    }

    /// <summary>
    /// After a branch's touched children have been walked, records the lone surviving sibling if the branch may
    /// collapse, and reports whether the whole branch may be emptied.
    /// </summary>
    private static bool CollapseCheck(
        ITrieNodeResolver resolver,
        TrieNode node,
        ref TreePath path,
        ReadOnlySpan<byte> childState,
        ISink sink)
    {
        int survivingCount = 0;
        int survivingIndex = -1;
        bool survivorTraversed = false;
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            if (childState[i] == MaybeEmptied) continue;
            if (childState[i] == Untraversed && node.IsChildNull(i)) continue;
            survivingCount++;
            if (survivingCount > 1) return false; // >= 2 survivors: the branch cannot collapse
            survivingIndex = i;
            survivorTraversed = childState[i] == Survived;
        }

        if (survivingCount == 0) return true;

        // One survivor: an order that empties the rest collapses the branch into it. Report the survivor if it was untouched, since the verifier needs it to recompute that collapse.
        if (!survivorTraversed)
        {
            path.AppendMut(survivingIndex);
            TrieNode? sibling = node.GetChildWithChildPath(resolver, ref path, survivingIndex);
            if (sibling is not null)
            {
                sibling.ResolveNode(resolver, path);
                if (sibling.Keccak is not null) sink.Add(path, sibling);
            }
            path.TruncateOne();
        }
        return false;
    }

    private static int GetSpanOffset<T>(T[] array, Span<T> span)
    {
        ref T spanRef = ref MemoryMarshal.GetReference(span);
        ref T arrRef = ref MemoryMarshal.GetArrayDataReference(array);
        return (int)(Unsafe.ByteOffset(ref arrRef, ref spanRef) / Unsafe.SizeOf<T>());
    }
}
