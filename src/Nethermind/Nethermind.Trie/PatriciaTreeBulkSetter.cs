// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Prometheus;

namespace Nethermind.Trie;

public class PatriciaTreeBulkSetter(PatriciaTree patriciaTree)
{
    public const int MinEntriesToParallelizeThreshold = 256;

    [Flags]
    public enum Flags
    {
        None = 0,
        WasSorted = 1,
        DoNotParallelize = 2,
        CalculateRoot = 4,
    }

    public static void BulkSet(PatriciaTree patriciaTree, ArrayPoolList<BulkSetEntry> entriesMemory, Flags flags = Flags.None)
    {
        new PatriciaTreeBulkSetter(patriciaTree).BulkSet(entriesMemory, flags);
    }

    public readonly struct BulkSetEntry(ValueHash256 path, byte[] value) : IComparable<BulkSetEntry>
    {
        public readonly ValueHash256 Path = path;
        public readonly byte[] Value = value;

        public int CompareTo(BulkSetEntry entry)
        {
            return Path.CompareTo(entry.Path);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetPathNibbble(int index)
        {
            int offset = index / 2;
            Span<byte> theSpan = Path.BytesAsSpan;
            int b = theSpan[offset];
            if ((index & 1) == 0)
            {
                return (byte)((b & 0xf0) >> 4);
            }
            else
            {
                return (byte)(b & 0x0f);
            }
        }
    }

    private static Counter _bulkSetCounter = Prometheus.Metrics.CreateCounter("bulksetter_bulk_set", "Get count");
    private static Counter _bulkSetRecurCounter = Prometheus.Metrics.CreateCounter("bulksetter_bulk_set_recur", "Get count", "type");

    /// <summary>
    /// BulkSet multiple entries at the same time. It works by working each nibble level one at a time, partially
    /// sorting the <see cref="entriesMemory"/> then recurs on each nibble, traversing the top level branch only once.
    /// if <see cref="Flags.WasSorted"/> is on, the sort is skipped for a slightly faster set.
    /// It will parallelize at the top level if the number of entries reached a certain threshold.
    /// </summary>
    /// <param name="entriesMemory"></param>
    /// <param name="flags"></param>
    public void BulkSet(ArrayPoolList<BulkSetEntry> entriesMemory, Flags flags)
    {
        Span<BulkSetEntry> entries = entriesMemory.AsSpan();
        if (entries.Length == 0) return;

        _bulkSetCounter.Inc();

        TreePath path = TreePath.Empty;
        ThreadResource threadResource = GetThreadResource();
        using ArrayPoolList<BulkSetEntry> buffer = new ArrayPoolList<BulkSetEntry>(entries.Length, entries.Length);


        TrieNode? newRoot = BulkSet(
            threadResource,
            entriesMemory.UnsafeGetInternalArray(),
            buffer.UnsafeGetInternalArray(),
            entriesMemory.AsSpan(),
            buffer.AsSpan(),
            ref path, patriciaTree.RootRef, (flags & Flags.DoNotParallelize) == 0, flags);
        if (flags.HasFlag(Flags.CalculateRoot)) newRoot?.ResolveKey(patriciaTree.TrieStore, ref path, canBeParallel: false);
        patriciaTree.RootRef = newRoot;


        patriciaTree.IncrementWriteCount(entries.Length);
        ReturnThreadResource(threadResource);
    }

    internal TrieNode? BulkSet(
        ThreadResource threadResource,
        BulkSetEntry[] originalEntriesArray,
        BulkSetEntry[] originalBufferArray,
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> buffer,
        ref TreePath path,
        TrieNode? node,
        bool canParallelize,
        Flags flags)
    {
        TrieNode? originalNode = node;

        if (entries.Length == 1) return BulkSetOneStack(threadResource, in entries[0], ref path, node, flags);

        bool newBranch = false;
        if (node is null)
        {
            node = TrieNodeFactory.CreateBranch();
            newBranch = true;
        }
        else
        {
            node.ResolveNode(patriciaTree.TrieStore, path);

            _bulkSetRecurCounter.WithLabels(node.NodeType.ToString()).Inc();

            if (!node.IsBranch)
            {
                // TODO: Check if it is worth it. Maybe just call `BulkSetOneStack` multiple time.
                node = MakeFakeBranch(ref path, node);
                newBranch = true;
            }
        }

        Span<(int, int)> indexes = stackalloc (int, int)[TrieNode.BranchesCount];

        if (path.Length == 64) throw new InvalidOperationException("Non unique entry keys");

        int nibToCheck;
        if ((flags & Flags.WasSorted) != 0)
        {
            nibToCheck = HexarySearchAlreadySorted(entries, path.Length, indexes);
        }
        else
        {
            nibToCheck = BucketSort16(entries, buffer, path.Length, indexes);
            // Buffer is now partially sorted. Swap buffer and entries
            (originalEntriesArray, originalBufferArray) = (originalBufferArray, originalEntriesArray);

            Span<BulkSetEntry> newBufferSpan = entries;
            entries = buffer;
            buffer = newBufferSpan;
        }

        bool hasRemove = false;
        int nonNullChildCount = 0;
        if (entries.Length >= MinEntriesToParallelizeThreshold && nibToCheck == TrieNode.BranchesCount && canParallelize)
        {
            (int startIdx, int count, int nibble, TreePath appendedPath, TrieNode? currentChild, TrieNode? newChild, Flags flags)[] jobs =
                new (int startIdx, int count, int nibble, TreePath appendedPath, TrieNode? currentChild, TrieNode? newChild, Flags flags)[TrieNode.BranchesCount];

            TrieNode.ChildIterator childIterator = node.CreateChildIterator();
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                (int nib, int startRange) = indexes[i];

                int endRange;
                if (i < nibToCheck - 1)
                    endRange = indexes[i + 1].Item2;
                else
                    endRange = entries.Length;

                Span<BulkSetEntry> jobEntry = entries.Slice(startRange, endRange - startRange);

                TreePath childPath = path.Append(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(patriciaTree.TrieStore, ref childPath, nib);
                jobs[i] = (GetSpanOffset(originalEntriesArray, jobEntry), jobEntry.Length, nib, childPath, child, null, flags);
            }

            ParallelUnbalancedWork.For(0, nibToCheck,
                ParallelUnbalancedWork.DefaultOptions,
                GetThreadResource,
                (i, workerThreadResource) =>
                {
                    (int startIdx, int count, int nib, TreePath childPath, TrieNode child, TrieNode? outNode, Flags flags) = jobs[i];

                    Span<BulkSetEntry> jobEntries = originalEntriesArray.AsSpan().Slice(startIdx, count);
                    Span<BulkSetEntry> bufferEntries = originalBufferArray.AsSpan().Slice(startIdx, count);

                    TrieNode? newChild = BulkSet(workerThreadResource, originalEntriesArray, originalBufferArray, jobEntries, bufferEntries, ref childPath, child, false, flags); // Only parallelize at top level.
                    jobs[i] = (startIdx, count, nib, childPath, child, newChild, flags); // Just need the child actually...

                    return workerThreadResource;
                }, ReturnThreadResource);

            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? child = jobs[i].currentChild;
                TrieNode? newChild = jobs[i].newChild;

                if (!ShouldUpdateChild(child, newChild)) continue;

                if (newChild is null) hasRemove = true;
                if (newChild is not null) nonNullChildCount++;
                if (node.IsSealed) node = node.Clone();

                node.SetChild(i, newChild);
            }
        }
        else
        {
            TrieNode.ChildIterator childIterator = node.CreateChildIterator();
            path.AppendMut(0);
            for (int i = 0; i < nibToCheck; i++)
            {
                (int nib, int startRange) = indexes[i];

                path.SetLast(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(patriciaTree.TrieStore, ref path, nib);

                int endRange;
                if (i < nibToCheck - 1)
                    endRange = indexes[i + 1].Item2;
                else
                    endRange = entries.Length;

                TrieNode newChild = (endRange - startRange == 1)
                    ? BulkSetOneStack(threadResource, entries[startRange], ref path, child, flags)
                    : BulkSet(threadResource, originalEntriesArray, originalBufferArray, entries[startRange..endRange], buffer[startRange..endRange], ref path, child, canParallelize, flags);

                if (!ShouldUpdateChild(child, newChild)) continue;

                if (newChild is null) hasRemove = true;
                if (newChild is not null) nonNullChildCount++;
                if (node.IsSealed) node = node.Clone();

                node.SetChild(nib, newChild);
            }
            path.TruncateOne();
        }

        if (!hasRemove && nonNullChildCount == 0) return originalNode;

        if ((hasRemove || newBranch) && nonNullChildCount < 2)
            node = MaybeCombineNode(ref path, node);

        if ((flags & Flags.CalculateRoot) != 0) node?.ResolveKey(patriciaTree.TrieStore, ref path, canBeParallel: false);
        return node;
    }

    internal TrieNode? BulkSetOneStack(ThreadResource threadResource, in BulkSetEntry entry, ref TreePath path, TrieNode? node, Flags flags)
    {
        Span<byte> nibble = stackalloc byte[64];
        Nibbles.BytesToNibbleBytes(entry.Path.BytesAsSpan, nibble);
        Span<byte> remainingKey = nibble[path.Length..];

        Stack<TraverseStack> traverseStack = threadResource.TraverseStack;
        TrieNode? originalNode = node;
        int originalPathLength = path.Length;
        bool shouldUpdateRoot = flags.HasFlag(Flags.CalculateRoot);
        byte[] value = entry.Value;

        while (true)
        {
            if (node is null)
            {
                if (value is null || value.Length == 0)
                    node = null;
                else
                    node = TrieNodeFactory.CreateLeaf(remainingKey.ToArray(), value);

                // End traverse
                break;
            }

            node.ResolveNode(patriciaTree.TrieStore, path);

            if (node.IsLeaf || node.IsExtension)
            {
                int commonPrefixLength = remainingKey.CommonPrefixLength(node.Key);
                if (commonPrefixLength == node.Key!.Length)
                {
                    if (node.IsExtension)
                    {
                        // Continue traversal to the child of the extension
                        path.AppendMut(node.Key);
                        TrieNode? extensionChild = node.GetChildWithChildPath(patriciaTree.TrieStore, ref path, 0);

                        traverseStack.Push(new TraverseStack()
                        {
                            Node = node,
                            OriginalChild = extensionChild,
                            ChildIdx = 0,
                        });

                        // Continue loop with the child as current node
                        remainingKey = remainingKey[node!.Key.Length..];
                        node = extensionChild;

                        continue;
                    }

                    if (value is null || value.Length == 0)
                    {
                        // Deletion
                        node = null;
                    }
                    else if (node.Value.Equals(value))
                    {
                        // SHORTCUT!
                        path.TruncateMut(originalPathLength);
                        traverseStack.Clear();
                        return originalNode;
                    }
                    else if (node.IsSealed)
                    {
                        node = TrieNodeFactory.CreateLeaf(remainingKey.ToArray(), value);
                    }
                    else
                    {
                        node.Value = value;
                    }

                    if (shouldUpdateRoot) node?.ResolveKey(patriciaTree.TrieStore, ref path, canBeParallel: false);
                    // end traverse
                    break;
                }

                // We are suppose to create a branch, but no change in structure
                if (value is null || value.Length == 0)
                {
                    // end traverse
                    break;
                }

                // Making a T branch here.
                // If the commonPrefixLength > 0, we'll also need to also make an extension in front of the branch.
                TrieNode theBranch = TrieNodeFactory.CreateBranch();

                // This is the current node branch
                int currentNodeNib = node.Key[commonPrefixLength];
                if (node.Key.Length == commonPrefixLength + 1)
                {
                    if (node.IsLeaf) throw new InvalidOperationException("Branch with value not supported");
                    // Collapsing the extension, taking the child directly and set the branch
                    int originalLength = path.Length;
                    path.AppendMut(node.Key);
                    path.AppendMut(currentNodeNib);
                    theBranch[currentNodeNib] = node.GetChildWithChildPath(patriciaTree.TrieStore, ref path, 0);
                    path.TruncateMut(originalLength);
                } else {
                    theBranch[currentNodeNib] = node.CloneWithChangedKey(node.Key.Slice(commonPrefixLength + 1));
                }

                // This is the new branch
                theBranch[remainingKey[commonPrefixLength]] =
                    TrieNodeFactory.CreateLeaf(remainingKey[(commonPrefixLength+1)..].ToArray(), value);

                // Extension in front of the branch
                node = commonPrefixLength == 0 ?
                    theBranch :
                    TrieNodeFactory.CreateExtension(remainingKey[..commonPrefixLength].ToArray(), theBranch);

                // This is the end of the traversal, which end in a branch/extension instead of leaf.
                // Since we know this is not a deletion, we do not need to check to combine the branch to extension.
                // Since this is originally a leaf or an extension so its parent must be a branch, so we can calculate
                // root here.
                if (shouldUpdateRoot) node?.ResolveKey(patriciaTree.TrieStore, ref path, canBeParallel: false);
                break;
            }

            int nib = remainingKey[0];
            path.AppendMut(nib);
            TrieNode? child = node.GetChildWithChildPath(patriciaTree.TrieStore, ref path, nib);

            traverseStack.Push(new TraverseStack()
            {
                Node = node,
                OriginalChild = child,
                ChildIdx = nib,
            });

            // Continue loop with child as current node
            node = child;
            remainingKey = remainingKey[1..];
        }

        while (traverseStack.TryPop(out TraverseStack cStack))
        {
            TrieNode? child = node;
            node = cStack.Node;

            if (node.IsExtension)
            {
                path.TruncateMut(path.Length - node.Key!.Length);

                if (ShouldUpdateChild(child, cStack.OriginalChild))
                {
                    if (child is null)
                    {
                        node = null; // Remove extension
                        continue;
                    }

                    if (child.IsExtension || child.IsLeaf)
                    {
                        // Merge current node with child
                        node = child.CloneWithChangedKey(Bytes.Concat(node.Key, child.Key));
                    }
                    else
                    {
                        if (node.IsSealed) node = node.Clone();
                        node.SetChild(0, child);
                    }

                    // Extension must be a branch parent.
                    // Since this is not a deletion, parent will not get merged to an extension so it is safe to
                    // recalculate root
                    if (shouldUpdateRoot) node?.ResolveKey(patriciaTree.TrieStore, ref path, canBeParallel: false);
                }

                continue;
            }

            // Branch only
            int nib = cStack.ChildIdx;

            bool hasRemove = false;
            path.TruncateOne();

            if (ShouldUpdateChild(child, cStack.OriginalChild))
            {
                if (child is null) hasRemove = true;
                if (node.IsSealed) node = node.Clone();

                node.SetChild(nib, child);
            }

            if (!hasRemove)
            {
                if (shouldUpdateRoot) node?.ResolveKey(patriciaTree.TrieStore, ref path, canBeParallel: false);
                continue;
            }

            node = MaybeCombineNode(ref path, node);
            if (node?.IsExtension == false)
            {
                // If it is an extension, it might get merged with parent, so we don't recalculate root
                if (shouldUpdateRoot) node?.ResolveKey(patriciaTree.TrieStore, ref path, canBeParallel: false);
            }
        }

        return node;
    }

    private bool ShouldUpdateChild(TrieNode? child1, TrieNode? child2)
    {
        if (child1 is null && child2 is null) return false;
        return !ReferenceEquals(child1, child2);
    }

    private TrieNode? MakeFakeBranch(ref TreePath currentPath, TrieNode? existingNode)
    {
        // TODO: if this is a leaf, the existing key is long. Check if it is worth optimizing.
        byte[] shortenedKey = new byte[existingNode.Key.Length - 1];
        Array.Copy(existingNode.Key, 1, shortenedKey, 0, existingNode.Key.Length - 1);

        int branchIdx = existingNode.Key[0];

        TrieNode newChild;
        if (existingNode.IsLeaf)
        {
            newChild = TrieNodeFactory.CreateLeaf(shortenedKey, existingNode.Value);
        }
        else
        {
            var child = existingNode.GetChild(patriciaTree.TrieStore, ref currentPath, 0);
            if (existingNode.Key.Length == 1)
            {
                newChild = child;
            }
            else
            {
                newChild = TrieNodeFactory.CreateExtension(shortenedKey, child);
            }
        }

        existingNode = TrieNodeFactory.CreateBranch();
        existingNode.SetChild(branchIdx, newChild);

        return existingNode;
    }

    /// <summary>
    /// Tries to make the current node an extension or null if it has only one child left.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    private TrieNode? MaybeCombineNode(ref TreePath path, in TrieNode? node)
    {
        int onlyChildIdx = -1;
        TrieNode? onlyChildNode = null;
        path.AppendMut(0);
        var iterator = node.CreateChildIterator();
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            path.SetLast(i);
            TrieNode? child = iterator.GetChildWithChildPath(patriciaTree.TrieStore, ref path, i);

            if (child is not null)
            {
                if (onlyChildIdx == -1)
                {
                    onlyChildIdx = i;
                    onlyChildNode = child;
                }
                else
                {
                    // More than one non null child. We don't care anymore.
                    path.TruncateOne();
                    return node;
                }
            }

        }
        path.TruncateOne();

        if (onlyChildIdx == -1) return null; // No child at all.

        path.AppendMut(onlyChildIdx);
        onlyChildNode.ResolveNode(patriciaTree.TrieStore, path);
        path.TruncateOne();

        if (onlyChildNode.IsBranch)
        {
            return TrieNodeFactory.CreateExtension([(byte)onlyChildIdx], onlyChildNode);
        }

        // Replace the only child with something with extra key.
        byte[] newKey = Bytes.Concat((byte)onlyChildIdx, onlyChildNode.Key);
        TrieNode tn = onlyChildNode.CloneWithChangedKey(newKey);
        return tn;
    }

    /// <summary>
    /// Partially sort the <see cref="entries"/> based on the nibble at <see cref="pathIndex"/>> while at the same time
    /// populate <see cref="indexes"/> similar to <see cref="HexarySearchAlreadySorted"/>. Output is set to <see cref="sortTarget"/>.
    /// </summary>
    /// <param name="entries"></param>
    /// <param name="sortTarget"></param>
    /// <param name="pathIndex"></param>
    /// <param name="indexes"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    internal static int BucketSort16(
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> sortTarget,
        int pathIndex,
        Span<(int, int)> indexes)
    {

#if DEBUG
        if (entries.Length != sortTarget.Length) throw new Exception("Both buffer must be of the same length");
        if (threadResource.SortBuckets.Count != 16) throw new Exception("Sort bucckets must be of length 16");
        foreach (ArrayPoolList<int> bucket in threadResource.SortBuckets) if (bucket.Count != 0) throw new Exception("Bucket must be emptied");
#endif

        if (entries.Length < 24)
        {
            return BucketSort16Small(entries, sortTarget, pathIndex, indexes);
        }

        return BucketSort16Large(entries, sortTarget, pathIndex, indexes);
    }

    private static int BucketSort16Large(
        Span<BulkSetEntry> entries, Span<BulkSetEntry> sortTarget, int pathIndex, Span<(int, int)> indexes)
    {
        // You know, I originally used another buffer to keep track of the entries per nibble. then ChatGPT gave me this.
        // I dont know what is worst, that ChatGPT beat me to it, or that it is simpler.

        Span<int> counts = stackalloc int[TrieNode.BranchesCount];
        for (int i = 0; i < entries.Length; i++)
            counts[entries[i].GetPathNibbble(pathIndex)]++;

        Span<int> starts = stackalloc int[TrieNode.BranchesCount];
        int relevantNib = 0;
        int total = 0;
        for (int n = 0; n < TrieNode.BranchesCount; n++)
        {
            starts[n] = total;
            total += counts[n];
            if (counts[n] != 0)
                indexes[relevantNib++] = (n, starts[n]);
        }

        for (int i = 0; i < entries.Length; i++)
        {
            int nib = entries[i].GetPathNibbble(pathIndex);
            sortTarget[starts[nib]++] = entries[i];
        }

        return relevantNib;
    }

    private static int BucketSort16Small(
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> sortTarget,
        int pathIndex,
        Span<(int, int)> indexes)
    {
        // The small variant keeps track of used nibbles to skip looping unused nibble.
        int relevantNib = 0;
        int usedMask = 0;

        Span<byte> nibbleCache = stackalloc byte[entries.Length];

        Span<int> counts = stackalloc int[TrieNode.BranchesCount];
        for (int i = 0; i < entries.Length; i++)
        {
            byte nib = entries[i].GetPathNibbble(pathIndex);
            nibbleCache[i] = nib;
            counts[nib]++;
            usedMask |= 1 << nib;
        }

        Span<int> starts = stackalloc int[TrieNode.BranchesCount];
        int total = 0;
        int mask = usedMask;
        while (mask != 0)
        {
            int nib = BitOperations.TrailingZeroCount(mask);

            starts[nib] = total;
            total += counts[nib];
            indexes[relevantNib++] = (nib, starts[nib]);

            mask &= mask - 1; // clear lowest 1-bit
        }

        for (int i = 0; i < entries.Length; i++)
        {
            int nib = nibbleCache[i];
            sortTarget[starts[nib]++] = entries[i];
        }

        return relevantNib;
    }

    /// <summary>
    /// Populate the <see cref="indexes"/> to the starting index of each nibble. It skip nibble that is missing and
    /// returns the number of unique nibble. It assume <see cref="entries"/> is already sorted.
    /// </summary>
    /// <param name="entries"></param>
    /// <param name="pathIndex"></param>
    /// <param name="indexes"></param>
    /// <returns></returns>
    public static int HexarySearchAlreadySorted(Span<BulkSetEntry> entries, int pathIndex, Span<(int, int)> indexes)
    {
        // TODO: Check if it worth it to convert to binary search.
        int curIdx = 0;
        int relevantNib = 0;

        for (int i = 0; i < entries.Length && curIdx < TrieNode.BranchesCount; i++)
        {
            var currentNib = entries[i].GetPathNibbble(pathIndex);

            if (currentNib > curIdx)
            {
                curIdx = currentNib;
            }

            if (currentNib == curIdx)
            {
                indexes[relevantNib] = (currentNib, i);
                relevantNib++;
                curIdx++;
            }
        }

        return relevantNib;
    }

    internal struct TraverseStack
    {
        public TrieNode Node;
        public int ChildIdx;
        public TrieNode? OriginalChild;
    }

    /// <summary>
    /// Some bunch of structures that are used often but cannot be shared between threads.
    /// </summary>
    internal class ThreadResource: IDisposable
    {
        /// <summary>
        /// Used for <see cref="PatriciaTreeBulkSetter.BulkSetOneStack"/> to keep track of traversed node.
        /// </summary>
        internal Stack<TraverseStack> TraverseStack;

        public ThreadResource()
        {
            TraverseStack = new Stack<TraverseStack>(16);
        }

        public void Dispose()
        {
        }

        public void EnsureCleared()
        {
            if (TraverseStack.Count != 0)
            {
                throw new InvalidOperationException("traverse stack must be cleared before returning");
            }
        }
    }

    [ThreadStatic]
    private static ThreadResource? _threadStaticPool;

    private ThreadResource GetThreadResource()
    {
        if (_threadStaticPool is not null)
        {
            ThreadResource threadResource = _threadStaticPool;
            _threadStaticPool = null;
            return threadResource;
        }
        else
        {
            return new ThreadResource();
        }
    }

    private void ReturnThreadResource(ThreadResource threadResource)
    {
#if DEBUG
        threadResource.EnsureCleared();
#endif

        _threadStaticPool = threadResource;
    }

    public static int GetSpanOffset<T>(T[] array, Span<T> span)
    {
        ref T spanRef = ref MemoryMarshal.GetReference(span);
        ref T arrRef  = ref MemoryMarshal.GetArrayDataReference(array);
        return (int)(Unsafe.ByteOffset(ref arrRef, ref spanRef) / Unsafe.SizeOf<T>());
    }
}
