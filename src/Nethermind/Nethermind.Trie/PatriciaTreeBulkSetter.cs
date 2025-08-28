// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection.Metadata;
using System.Threading.Tasks;
using DotNetty.Common.Internal;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;

namespace Nethermind.Trie;

public class PatriciaTreeBulkSetter(PatriciaTree patriciaTree)
{
    public static void BulkSet(PatriciaTree patriciaTree, Memory<BulkSetEntry> entriesMemory)
    {
        new PatriciaTreeBulkSetter(patriciaTree).BulkSet(entriesMemory);
    }

    public static void BulkSetUnsorted(PatriciaTree patriciaTree, Memory<BulkSetEntry> entriesMemory, int targetBucketSize = 64)
    {
        if (entriesMemory.Length < targetBucketSize)
        {
            entriesMemory.Span.Sort();
            BulkSet(patriciaTree, entriesMemory);
            return;
        }

        int powerOfTwoSize = BitOperations.Log2((uint)(entriesMemory.Length / targetBucketSize));
        if (powerOfTwoSize == 0) powerOfTwoSize = 1; // Just make the code simpler
        if (powerOfTwoSize > 16) powerOfTwoSize = 16; // I mean, thats 65K
        int shift = 32 - powerOfTwoSize;
        int bucketCount = 1 << powerOfTwoSize;

        ArrayPoolList<ArrayPoolList<BulkSetEntry>> buckets = new ArrayPoolList<ArrayPoolList<BulkSetEntry>>(bucketCount, bucketCount);
        for (int i = 0; i < buckets.Count; i++)
        {
            buckets[i] = new ArrayPoolList<BulkSetEntry>(targetBucketSize);
        }

        Span<BulkSetEntry> asSpan = entriesMemory.Span;
        for (int i = 0; i < asSpan.Length; i++)
        {
            BulkSetEntry entry = asSpan[i];
            uint bucketIdx = entry._first4Byte >> shift;
            buckets[(int)bucketIdx].Add(entry);
        }

        ParallelUnbalancedWork.For(0, bucketCount, (i) => buckets[i].AsSpan().Sort());

        using ArrayPoolList<BulkSetEntry> sorted = new ArrayPoolList<BulkSetEntry>(entriesMemory.Length);
        for (int i = 0; i < buckets.Count; i++)
        {
            ArrayPoolList<BulkSetEntry> bucket = buckets[i];
            foreach (var bulkSetEntry in bucket)
            {
                sorted.Add(bulkSetEntry);
            }
            bucket.Dispose();
        }

        BulkSet(patriciaTree, sorted.AsMemory());
    }

    public readonly struct BulkSetEntry(ValueHash256 path, byte[] value) : IComparable<BulkSetEntry>
    {
        public readonly ValueHash256 Path = path;
        public readonly byte[] Value = value;
        internal readonly uint _first4Byte = BinaryPrimitives.ReadUInt32BigEndian(path.BytesAsSpan[..4]);

        public int CompareTo(BulkSetEntry entry)
        {
            int diff = _first4Byte.CompareTo(entry._first4Byte);
            if (diff != 0) return diff;

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

    public void BulkSet(Memory<BulkSetEntry> entriesMemory)
    {
        Span<BulkSetEntry> entries = entriesMemory.Span;

        for (int i = 1; i < entries.Length; i++)
        {
            int compared = entries[i-1].CompareTo(entries[i]);
            if (compared == 0)
            {
                throw new InvalidOperationException("Entries must be unique");
            }
            if (compared > 0)
            {
                throw new InvalidOperationException("Entries must be sorted in increasing order");
            }
        }

        TreePath path = TreePath.Empty;
        Stack<TraverseStack> stack = new Stack<TraverseStack>();
        patriciaTree.RootRef = BulkSet(stack, entriesMemory, ref path, patriciaTree.RootRef, true);
    }

    internal TrieNode? BulkSet(Stack<TraverseStack> traverseStack, Memory<BulkSetEntry> entriesMemory, ref TreePath currentPath, TrieNode? existingNode, bool canParallelize)
    {
        Span<BulkSetEntry> entries = entriesMemory.Span;

        if (entries.Length == 0) return existingNode;
        if (entries.Length == 1) return BulkSetOneStack(traverseStack, entries[0], ref currentPath, existingNode);

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

            if (existingNode.IsSealed) existingNode = existingNode.Clone();
        }

        Span<(int, int)> indexes = stackalloc (int, int)[TrieNode.BranchesCount];

        int nibToCheck = HexarySearch(entries, currentPath.Length, indexes);

        bool hasRemove = false;
        if (entries.Length > 64 && nibToCheck == TrieNode.BranchesCount && canParallelize)
        {
            (Memory<BulkSetEntry> entries, int nibble, TreePath appendedPath, TrieNode? outNode)[] jobs =
                new (Memory<BulkSetEntry> entries, int nibble, TreePath appendedPath, TrieNode? outNode)[TrieNode.BranchesCount];

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

                TreePath childPath = currentPath.Append(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(patriciaTree.TrieStore, ref childPath, nib);
                jobs[i] = (jobEntry, nib, childPath, child);
            }

            ParallelUnbalancedWork.For(0, nibToCheck, ParallelUnbalancedWork.DefaultOptions, jobs,
                (i, jobs) =>
            {
                (Memory<BulkSetEntry> jobEntry, int nib, TreePath childPath, TrieNode child) = jobs[i];

                child = BulkSet(new Stack<TraverseStack>(), jobEntry, ref childPath, child, false); // Only parallelize at top level.
                jobs[i] = (jobEntry, nib, childPath, child);
                return jobs;
            });

            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TrieNode? child = jobs[i].outNode;
                if (child is null) hasRemove = true;
                existingNode.SetChild(i, child);
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

                child = (endRange - startRange == 1)
                    ? BulkSetOneStack(traverseStack, entries[startRange], ref currentPath, child)
                    : BulkSet(traverseStack, entriesMemory.Slice(startRange, endRange - startRange), ref currentPath, child, canParallelize);
                if (child is null) hasRemove = true;

                existingNode.SetChild(nib, child);
            }

            currentPath.TruncateOne();
        }

        if (hasRemove || newBranch)
            return MaybeCombineNode(ref currentPath, existingNode);

        return existingNode;
    }

    internal TrieNode? BulkSetOneStack(Stack<TraverseStack> traverseStack, BulkSetEntry entry, ref TreePath currentNodePath, TrieNode? currentNode)
    {
        // Just for holding the expanded nibble for the key
        Span<byte> nibble = stackalloc byte[64];
        Nibbles.BytesToNibbleBytes(entry.Path.BytesAsSpan, nibble);
        Span<byte> remainingKey = nibble[currentNodePath.Length..];
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
                        currentNode = null;
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
                currentNode = MakeFakeBranch(ref currentNodePath, currentNode);
                shouldCheckForBranchMerge = true;
            }

            if (currentNode.IsSealed)
            {
                currentNode = currentNode.Clone();
            }

            int nib = remainingKey[0];
            currentNodePath.AppendMut(nib);
            TrieNode? child = currentNode.GetChildWithChildPath(patriciaTree.TrieStore, ref currentNodePath, nib);

            traverseStack.Push(new TraverseStack()
            {
                Node = currentNode,
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

            if (child is null) shouldCheckForBranchMerge = true;

            currentNode.SetChild(nib, child);

            if (!shouldCheckForBranchMerge)
            {
                continue;
            }

            currentNode = MaybeCombineNode(ref currentNodePath, currentNode);
        }

        return currentNode;
    }

    internal struct TraverseStack
    {
        public TrieNode Node;
        public int ChildIdx;
        public bool ShouldCheckForBranchMerge;
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

    public static int HexarySearch(Span<BulkSetEntry> entries, int pathIndex, Span<(int, int)> indexes)
    {
        // About .5% of the time
        // TODO: Change to binary search
        int relevantNib = 0;
        int curIdx = 0;

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
