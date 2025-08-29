// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
    }

    public static void BulkSet(PatriciaTree patriciaTree, Memory<BulkSetEntry> entriesMemory, Flags flags = Flags.None)
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

        public int GetPathNibbble(int index)
        {
            int offset = index / 2;
            Span<byte> theSpan = Path.BytesAsSpan;
            int b = theSpan[offset];
            if ((index & 1) == 0)
            {
                return ((b & 0xf0) >> 4);
            }
            else
            {
                return (b & 0x0f);
            }
        }
    }

    private static Counter _bulkSetCounter = Prometheus.Metrics.CreateCounter("bulksetter_bulk_set", "Get count");

    // Pool some thread resource based on the size of the entry with a simple array
    private const int ThreadResourcePoolCount = 12;
    // But for lenght of < 2^this value, just use _threadStaticPool, which is a bit faster, but does not dispose.
    private const int ThreadResourceThreadStaticBit = 5;
    private static ThreadResource?[] _pooledThreadResource = new ThreadResource?[ThreadResourcePoolCount];

    [ThreadStatic]
    private static ThreadResource? _threadStaticPool;

    private ThreadResource GetThreadResource(int entrySize, Flags flags)
    {
        if (flags.HasFlag(Flags.WasSorted))
        {
            return new ThreadResource(entrySize, flags);
        }

        // Bucket for every power of 16
        int bucketIdx = BitOperations.Log2((uint)entrySize);
        if (bucketIdx > ThreadResourcePoolCount) return new ThreadResource(entrySize, flags); // Seems to be faster to just not pool

        if (bucketIdx <= ThreadResourceThreadStaticBit)
        {
            if (_threadStaticPool is not null)
            {
                ThreadResource threadResource = _threadStaticPool;
                _threadStaticPool = null;
                return threadResource;
            }
            else
            {
                return new ThreadResource(entrySize, flags);
            }
        }

        // Get and replace with null
        ThreadResource? originalValue = Interlocked.Exchange(ref _pooledThreadResource[bucketIdx], null);
        if (originalValue is not null) return originalValue;

        // Will have to just make a new one
        return new ThreadResource(entrySize, flags);
    }

    private void ReturnThreadResource(int entrySize, Flags flags, ThreadResource threadResource)
    {
        if (flags.HasFlag(Flags.WasSorted))
        {
            threadResource.Dispose();
            return;
        }

#if DEBUG
        threadResource.EnsureCleared();
#endif

        // Bucket for every power of 16
        int bucketIdx = BitOperations.Log2((uint)entrySize);
        if (bucketIdx > ThreadResourcePoolCount) {
            threadResource.Dispose();
            return;
        }

        if (bucketIdx <= ThreadResourceThreadStaticBit)
        {
            _threadStaticPool = threadResource;
            return;
        }

        ThreadResource? originalValue = Interlocked.Exchange(ref _pooledThreadResource[bucketIdx], threadResource);
        if (originalValue is not null)
        {
            originalValue.Dispose();
        }
    }

    public void BulkSet(Memory<BulkSetEntry> entriesMemory, Flags flags)
    {
        Span<BulkSetEntry> entries = entriesMemory.Span;
        if (entries.Length == 0) return;

        _bulkSetCounter.Inc();

        TreePath path = TreePath.Empty;
        ThreadResource threadResource = GetThreadResource(entries.Length, flags);
        using ArrayPoolList<BulkSetEntry> buffer = new ArrayPoolList<BulkSetEntry>(entries.Length, entries.Length);
        patriciaTree.RootRef = BulkSet(threadResource, entriesMemory, buffer.AsMemory(), ref path, patriciaTree.RootRef, (flags & Flags.DoNotParallelize) == 0, flags);
        patriciaTree.IncrementWriteCount(entries.Length);
        ReturnThreadResource(entries.Length, flags, threadResource);
    }

    internal class ThreadResource: IDisposable
    {
        internal Stack<TraverseStack> TraverseStack;
        internal ArrayPoolList<ArrayPoolList<int>>? SortBuckets;

        public ThreadResource(int entryCount, Flags flags)
        {
            if (!flags.HasFlag(Flags.WasSorted))
            {
                ArrayPoolList<ArrayPoolList<int>> sortCounters = new ArrayPoolList<ArrayPoolList<int>>(TrieNode.BranchesCount, TrieNode.BranchesCount);
                for (int i = 0; i < sortCounters.Count; i++)
                {
                    sortCounters[i] =
                        new ArrayPoolList<int>((int)BitOperations.RoundUpToPowerOf2((uint)(entryCount / TrieNode.BranchesCount)));
                }

                SortBuckets = sortCounters;
            }

            TraverseStack = new Stack<TraverseStack>(16);
        }

        public void Dispose()
        {
            if (SortBuckets is not null)
            {
                for (int i = 0; i < SortBuckets.Count; i++) SortBuckets[i].Dispose();
                SortBuckets.Dispose();
            }
        }

        public void EnsureCleared()
        {
            if (TraverseStack.Count != 0)
            {
                throw new InvalidOperationException("traverse stack must be cleared before returning");
            }
            foreach (ArrayPoolList<int> bucket in SortBuckets)
            {
                if (bucket.Count != 0)
                {
                    throw new InvalidOperationException("bucket must be cleared before returning");
                }
            }
        }
    }

    internal TrieNode? BulkSet(
        ThreadResource threadResource,
        Memory<BulkSetEntry> entriesMemory,
        Memory<BulkSetEntry> buffer,
        ref TreePath currentPath,
        TrieNode? existingNode,
        bool canParallelize,
        Flags flags)
    {
        Span<BulkSetEntry> entries = entriesMemory.Span;
        TrieNode? originalNode = existingNode;

        if (entries.Length == 1) return BulkSetOneStack(threadResource.TraverseStack, entries[0], ref currentPath, existingNode);

        bool newBranch = false;
        if (existingNode is null)
        {
            existingNode = TrieNodeFactory.CreateBranch();
            newBranch = true;
        }
        else
        {
            existingNode.ResolveNode(patriciaTree.TrieStore, currentPath);

            if (!existingNode.IsBranch)
            {
                existingNode = MakeFakeBranch(ref currentPath, existingNode);
                newBranch = true;
            }
        }

        Span<(int, int)> indexes = stackalloc (int, int)[TrieNode.BranchesCount];

        if (currentPath.Length == 64)
        {
            throw new InvalidOperationException("Non unique entry keys");
        }

        int nibToCheck;
        if ((flags & Flags.WasSorted) != 0)
        {
            nibToCheck = HexarySearchAlreadySorted(entries, currentPath.Length, indexes);
        }
        else
        {
            nibToCheck = BucketSort16(entries, buffer.Span, currentPath.Length, indexes, threadResource.SortBuckets.UnsafeGetInternalArray());
            // Buffer is not partially sorted. Swap buffer and entries
            Memory<BulkSetEntry> newBuffer = entriesMemory;
            entriesMemory = buffer;
            entries = entriesMemory.Span;
            buffer = newBuffer;
        }

        bool hasRemove = false;
        int nonNullChild = 0;
        bool hasChange = false;
        if (entries.Length >= MinEntriesToParallelizeThreshold && nibToCheck == TrieNode.BranchesCount && canParallelize)
        {
            (Memory<BulkSetEntry> entries, Memory<BulkSetEntry> buffer, int nibble, TreePath appendedPath, TrieNode? currentChild, TrieNode? newChild, Flags flags)[] jobs =
                new (Memory<BulkSetEntry> entries, Memory<BulkSetEntry> buffer, int nibble, TreePath appendedPath, TrieNode? currentChild, TrieNode? newChild, Flags flags)[TrieNode.BranchesCount];

            TrieNode.ChildIterator childIterator = existingNode.CreateChildIterator();
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                (int nib, int startRange) = indexes[i];

                int endRange;
                if (i < nibToCheck - 1)
                {
                    endRange = indexes[i + 1].Item2;
                }
                else
                {
                    endRange = entries.Length;
                }

                Memory<BulkSetEntry> jobEntry = entriesMemory.Slice(startRange, endRange - startRange);
                Memory<BulkSetEntry> jobBuffer = buffer.Slice(startRange, endRange - startRange);

                TreePath childPath = currentPath.Append(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(patriciaTree.TrieStore, ref childPath, nib);
                jobs[i] = (jobEntry, jobBuffer, nib, childPath, child, null, flags);
            }

            bool outerWasUsed = false;
            ParallelUnbalancedWork.For(0, nibToCheck,
                ParallelUnbalancedWork.DefaultOptions,
                () =>
                {
                    if (!Interlocked.CompareExchange(ref outerWasUsed, true, false))
                    {
                        return threadResource;
                    }

                    return GetThreadResource(entriesMemory.Length / 16, flags);
                },
                (i, workerThreadResource) =>
                {
                    (Memory<BulkSetEntry> jobEntry, Memory<BulkSetEntry> buffer, int nib, TreePath childPath, TrieNode child, TrieNode? outNode, Flags flags) = jobs[i];

                    TrieNode? newChild = BulkSet(workerThreadResource, jobEntry, buffer, ref childPath, child, false, flags); // Only parallelize at top level.
                    jobs[i] = (jobEntry, buffer, nib, childPath, child, newChild, flags); // Just need the child actually...

                    return workerThreadResource;
                }, (workerThreadResource =>
                {
                    if (ReferenceEquals(workerThreadResource, threadResource)) return;
                    ReturnThreadResource(entriesMemory.Length / 16, flags, workerThreadResource);
                }));

            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? child = jobs[i].currentChild;
                TrieNode? newChild = jobs[i].newChild;

                if (ShouldUpdateChild(child, newChild))
                {
                    if (newChild is null) hasRemove = true;
                    if (newChild is not null) nonNullChild++;
                    if (existingNode.IsSealed) existingNode = existingNode.Clone();

                    existingNode.SetChild(i, newChild);
                    hasChange = true;
                }
            }
        }
        else
        {
            TrieNode.ChildIterator childIterator = existingNode.CreateChildIterator();
            currentPath.AppendMut(0);
            for (int i = 0; i < nibToCheck; i++)
            {
                (int nib, int startRange) = indexes[i];

                currentPath.SetLast(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(patriciaTree.TrieStore, ref currentPath, nib);

                int endRange;
                if (i < nibToCheck - 1)
                {
                    endRange = indexes[i + 1].Item2;
                }
                else
                {
                    endRange = entries.Length;
                }

                var newChild = (endRange - startRange == 1)
                    ? BulkSetOneStack(threadResource.TraverseStack, entries[startRange], ref currentPath, child)
                    : BulkSet(threadResource, entriesMemory[startRange..endRange], buffer[startRange..endRange], ref currentPath, child, canParallelize, flags);

                if (ShouldUpdateChild(child, newChild))
                {
                    if (newChild is null) hasRemove = true;
                    if (newChild is not null) nonNullChild++;
                    if (existingNode.IsSealed) existingNode = existingNode.Clone();

                    existingNode.SetChild(nib, newChild);
                    hasChange = true;
                }
            }

            currentPath.TruncateOne();
        }

        if (!hasChange)
        {
            return originalNode;
        }

        if ((hasRemove || newBranch) && nonNullChild < 2)
            return MaybeCombineNode(ref currentPath, existingNode);

        return existingNode;
    }

    internal TrieNode? BulkSetOneStack(Stack<TraverseStack> traverseStack, BulkSetEntry entry, ref TreePath currentNodePath, TrieNode? currentNode)
    {
        // Just for holding the expanded nibble for the key
        Span<byte> nibble = stackalloc byte[64];
        Nibbles.BytesToNibbleBytes(entry.Path.BytesAsSpan, nibble);
        Span<byte> remainingKey = nibble[currentNodePath.Length..];
        TrieNode? originalNode = currentNode;
        int originalPathLength = currentNodePath.Length;
        byte[] value = entry.Value;

        while (true)
        {
            if (currentNode is null)
            {
                if (value is null || value.Length == 0)
                    currentNode = null;
                else
                    currentNode = TrieNodeFactory.CreateLeaf(remainingKey.ToArray(), value);

                // End traverse
                break;
            }

            currentNode.ResolveNode(patriciaTree.TrieStore, currentNodePath);

            if (currentNode.IsLeaf)
            {
                int commonPrefixLength = remainingKey.CommonPrefixLength(currentNode.Key);
                if (commonPrefixLength == currentNode.Key!.Length)
                {
                    if (value is null || value.Length == 0)
                    {
                        // Deletion
                        currentNode = null;
                    }
                    else if (currentNode.Value.Equals(value))
                    {
                        // SHORTCUT!
                        currentNodePath.TruncateMut(originalPathLength);
                        traverseStack.Clear();
                        return originalNode;
                    }
                    else if (currentNode.IsSealed)
                    {
                        currentNode = TrieNodeFactory.CreateLeaf(remainingKey.ToArray(), value);
                    }
                    else
                    {
                        currentNode.Value = value;
                    }

                    // end traverse
                    break;
                }

                // No change in structure
                if (value is null || value.Length == 0)
                {
                    // end traverse
                    break;
                }

                // Making a T branch here.
                // If the commonPrefixLength > 0, we'll also need to also make an extension in front of the branch.
                TrieNode theBranch = TrieNodeFactory.CreateBranch();
                theBranch[currentNode.Key[commonPrefixLength]] =
                    TrieNodeFactory.CreateLeaf(currentNode.Key.Slice(commonPrefixLength + 1), currentNode.Value);
                theBranch[remainingKey[commonPrefixLength]] =
                    TrieNodeFactory.CreateLeaf(remainingKey[(commonPrefixLength+1)..].ToArray(), value);

                if (commonPrefixLength == 0)
                {
                    currentNode = theBranch;
                }
                else
                {
                    currentNode = TrieNodeFactory.CreateExtension(remainingKey[..commonPrefixLength].ToArray(), theBranch);
                }

                // end traverse
                break;
            }

            // We make a fake branch
            bool shouldCheckForBranchMerge = false;
            if (currentNode.IsExtension)
            {
                // Happens about 0.45% of all iteration
                currentNode = MakeFakeBranch(ref currentNodePath, currentNode);
                shouldCheckForBranchMerge = true;
            }

            int nib = remainingKey[0];
            currentNodePath.AppendMut(nib);
            TrieNode? child = currentNode.GetChildWithChildPath(patriciaTree.TrieStore, ref currentNodePath, nib);

            traverseStack.Push(new TraverseStack()
            {
                Node = currentNode,
                OriginalChild = child,
                ChildIdx = nib,
                ShouldCheckForBranchMerge = shouldCheckForBranchMerge
            });

            // Continue loop with child as current node
            currentNode = child;
            remainingKey = remainingKey[1..];
        }

        while (traverseStack.TryPop(out TraverseStack cStack))
        {
            TrieNode? child = currentNode;
            currentNode = cStack.Node;
            bool shouldCheckForBranchMerge = cStack.ShouldCheckForBranchMerge;
            int nib = cStack.ChildIdx;

            currentNodePath.TruncateOne();

            if (ShouldUpdateChild(child, cStack.OriginalChild))
            {
                if (child is null) shouldCheckForBranchMerge = true;
                if (currentNode.IsSealed) currentNode = currentNode.Clone();

                currentNode.SetChild(nib, child);
            }

            if (!shouldCheckForBranchMerge)
            {
                continue;
            }

            currentNode = MaybeCombineNode(ref currentNodePath, currentNode);
        }

        return currentNode;
    }

    private bool ShouldUpdateChild(TrieNode? child1, TrieNode? child2)
    {
        if (child1 is null && child2 is null) return false;
        return !ReferenceEquals(child1, child2);
    }

    internal struct TraverseStack
    {
        public TrieNode Node;
        public int ChildIdx;
        public bool ShouldCheckForBranchMerge;
        public TrieNode? OriginalChild;
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

    private TrieNode? MaybeCombineNode(ref TreePath currentPath, in TrieNode? existingNode)
    {
        // Called, about 0.5% of the time (count)
        // Take about 2% of total time (second)
        int onlyChildIdx = -1;
        currentPath.AppendMut(0);
        var iterator = existingNode.CreateChildIterator();
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            currentPath.SetLast(i);
            TrieNode? child = iterator.GetChildWithChildPath(patriciaTree.TrieStore, ref currentPath, i);

            if (child is not null)
            {
                if (onlyChildIdx == -1)
                {
                    onlyChildIdx = i;
                }
                else
                {
                    // More than one non null child. We don't care anymore.
                    currentPath.TruncateOne();
                    return existingNode;
                }
            }

        }
        currentPath.TruncateOne();
        if (onlyChildIdx == -1) return null;


        currentPath.AppendMut(onlyChildIdx);
        TrieNode onlyChildNode = existingNode.GetChildWithChildPath(patriciaTree.TrieStore, ref currentPath, onlyChildIdx);
        onlyChildNode.ResolveNode(patriciaTree.TrieStore, currentPath);
        currentPath.TruncateOne();

        if (onlyChildNode.IsBranch)
        {
            return TrieNodeFactory.CreateExtension([(byte)onlyChildIdx], onlyChildNode);
        }

        // Replace the only child with something with extra key.
        // TODO: Check if it is worth optimizing
        byte[] newKey = new byte[onlyChildNode.Key.Length + 1];
        newKey[0] = (byte)onlyChildIdx;
        Array.Copy(onlyChildNode.Key, 0, newKey, 1, onlyChildNode.Key.Length);

        if (onlyChildNode.IsLeaf) return TrieNodeFactory.CreateLeaf(newKey, onlyChildNode.Value);
        return TrieNodeFactory.CreateExtension(newKey, onlyChildNode.GetChild(patriciaTree.TrieStore, ref currentPath, 0));
    }

    internal static int BucketSort16(Span<BulkSetEntry> entries, Span<BulkSetEntry> sortTarget, int pathIndex,
        Span<(int, int)> indexes, ArrayPoolList<int>[] idxLists)
    {
        if (entries.Length != sortTarget.Length) throw new Exception("Both buffer must be of the smae length");
        if (idxLists.Length != 16) throw new Exception("Idx list must be of length 16");
        foreach (ArrayPoolList<int> bucket in idxLists)
        {
            if (bucket.Count != 0) throw new Exception("Bucket must be emptied");
        }
        if (entries.Length < 24)
        {
            return BucketSort16Small(entries, sortTarget, pathIndex, indexes, idxLists);
        }

        for (int i = 0; i < entries.Length; i++)
        {
            var currentNib = entries[i].GetPathNibbble(pathIndex);
            idxLists[currentNib].Add(i);
        }

        int relevantNib = 0;
        int runningCount = 0;
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            ArrayPoolList<int> currentBucket = idxLists[i];

            if (currentBucket.Count > 0)
            {
                indexes[relevantNib] = (i, runningCount);
                relevantNib++;
            }

            for (int j = 0; j < currentBucket.Count; j++)
            {
                sortTarget[runningCount++] = entries[currentBucket[j]];
            }

            currentBucket.Clear();
        }

        return relevantNib;
    }

    private static int BucketSort16Small(Span<BulkSetEntry> entries, Span<BulkSetEntry> sortTarget, int pathIndex,
        Span<(int, int)> indexes, ArrayPoolList<int>[] idxLists)
    {
        // Small variant keep track of used nib to prevent looping through 16 possible nib.

        int usedNib = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var currentNib = entries[i].GetPathNibbble(pathIndex);
            usedNib |= 1 << currentNib;
            idxLists[currentNib].Add(i);
        }

        int relevantNib = 0;
        int runningCount = 0;
        while (usedNib != 0)
        {
            int i = BitOperations.TrailingZeroCount(usedNib);

            var currentBucket = idxLists[i];

            indexes[relevantNib] = (i, runningCount);
            relevantNib++;

            for (int j = 0; j < currentBucket.Count; j++)
            {
                sortTarget[runningCount++] = entries[currentBucket[j]];
            }

            currentBucket.Clear();

            usedNib &= usedNib - 1;
        }

        return relevantNib;
    }

    public static int HexarySearchAlreadySorted(Span<BulkSetEntry> entries, int pathIndex, Span<(int, int)> indexes)
    {
        // About .5% of the time
        // TODO: Change to binary search
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
}
