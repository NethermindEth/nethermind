// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie.Pruning;
using RuntimeInformation = Nethermind.Core.Cpu.RuntimeInformation;

namespace Nethermind.Trie;

/// <summary>
/// Hashes a trie's dirty nodes one path length at a time, deepest first, so that nodes which
/// become ready together share a batch Keccak kernel.
/// </summary>
/// <remarks>
/// The encode-driven walk hashes a node as soon as its own children are known. A commit that
/// touches scattered keys therefore leaves most parents holding a single dirty child with nothing
/// to pair it with, which is where roughly three quarters of the node hashes in a sparse commit go.
/// Two nodes at the same path length are never each other's ancestor, so a whole level can be
/// encoded and then handed to one kernel; deepest first is the order that leaves every node's
/// children already hashed by the time it is encoded. The root's dirty children own disjoint
/// subtrees, so each takes its own level order and they run in parallel, which is where the
/// encode-driven walk gets its throughput and what a single whole-tree level order would give up.
/// </remarks>
internal static class DirtyNodeHasher
{
    private const int MaxPathLength = 64;
    private const int BranchChildCount = 16;
    private const int HashBatchSize = 8;
    private const int Avx2HashBatchSize = 4;
    private const int MinimumAvx512BatchCount = 2;
    private const int VectorByteLength = 32;
    private const int MaxPaddedClass = KeccakHash.MaxBatchablePaddedLength / KeccakHash.RateBlockLength;

    /// <summary>Fewest dirty nodes in a subtree worth a level order.</summary>
    /// <remarks>Below this the collection walk costs more than the batching it could enable.</remarks>
    private const int MinimumDirtyNodes = 4;

    /// <summary>Most nodes one subtree's level order holds, which bounds what it costs in memory.</summary>
    /// <remarks>Each of the root's dirty subtrees holds its own, a few MB of pooled arrays at the cap,
    /// and up to sixteen of them can be held at once. A commit past this is a bulk heal rather than a
    /// block: the level order hashes as much as the budget holds and leaves the rest to the ordinary walk.</remarks>
    internal const int MaxCollectedNodes = 1 << 16;

    [InlineArray(HashBatchSize * (KeccakHash.MaxBatchablePaddedLength + Hash256.Size) / VectorByteLength)]
    private struct HashBuffer
    {
        private Vector256<byte> _element0;
    }

    private readonly struct PendingNode(TrieNode node, in TreePath path)
    {
        public readonly TrieNode Node = node;
        public readonly TreePath Path = path;
    }

    /// <summary>Hashes every dirty node below <paramref name="root" />, leaving the root itself alone.</summary>
    /// <remarks>Anything this leaves unhashed the caller's own walk still hashes, so declining any
    /// part of the work is always safe. The root is excluded because it is hashed even when its RLP
    /// is too short to be referenced by hash.</remarks>
    /// <returns>Whether every dirty child of the root came out hashed, so the caller's walk has
    /// nothing left to spread over cores.</returns>
    /// <param name="maxCollectedNodes">Collection budget for one subtree; only tests pass anything else.</param>
    internal static bool HashBelowRoot(TrieNode root, ITrieNodeResolver resolver, ICappedArrayPool? pool, bool canBeParallel,
        int maxCollectedNodes = MaxCollectedNodes)
    {
        if (!Avx2.IsSupported || !root.IsDirty || root.Keccak is not null) return false;

        if (!canBeParallel || RuntimeInformation.IsSingleProcessor || !root.IsBranch
            || !TryHashSubtreesInParallel(root, resolver, pool, maxCollectedNodes))
        {
            TreePath rootPath = TreePath.Empty;
            HashSubtree(root, in rootPath, resolver, pool, maxCollectedNodes);
        }

        return AllChildrenHashed(root);
    }

    private static bool AllChildrenHashed(TrieNode root)
    {
        // A leaf root has no children to ask about, and asking throws.
        int childCount = root.IsBranch ? BranchChildCount : root.IsExtension ? 1 : 0;
        for (int i = 0; i < childCount; i++)
        {
            if (root.TryGetDirtyChild(i, out TrieNode? child) && child.Keccak is null) return false;
        }

        return true;
    }

    /// <returns>Whether there were enough subtrees to be worth spreading; one is cheaper in place.</returns>
    private static bool TryHashSubtreesInParallel(TrieNode root, ITrieNodeResolver resolver, ICappedArrayPool? pool, int maxCollectedNodes)
    {
        const int MinimumSubtrees = 2;
        uint dirtyChildren = 0;
        for (int i = 0; i < BranchChildCount; i++)
        {
            if (root.TryGetDirtyChild(i, out TrieNode? child) && child.Keccak is null) dirtyChildren |= 1u << i;
        }

        // Counted before anything is allocated, so declining costs nothing on the common storage commit.
        int count = BitOperations.PopCount(dirtyChildren);
        if (count < MinimumSubtrees) return false;

        int[] childIndexes = new int[count];
        for (int i = 0; i < count; i++)
        {
            childIndexes[i] = BitOperations.TrailingZeroCount(dirtyChildren);
            dirtyChildren &= dirtyChildren - 1;
        }

        ParallelUnbalancedWork.For(0, count, RuntimeInformation.ParallelOptionsLogicalCores,
            (childIndexes, root, resolver, pool, maxCollectedNodes),
            static (i, state) =>
            {
                int childIndex = state.childIndexes[i];
                state.root.TryGetDirtyChild(childIndex, out TrieNode? child);
                TreePath childPath = TreePath.Empty;
                childPath.AppendMut(childIndex);
                HashSubtree(child!, in childPath, state.resolver, state.pool, state.maxCollectedNodes);
                return state;
            });

        return true;
    }

    private static void HashSubtree(TrieNode subtreeRoot, in TreePath subtreeRootPath, ITrieNodeResolver resolver, ICappedArrayPool? pool, int maxCollectedNodes)
    {
        using ArrayPoolList<PendingNode> pending = new(64);
        TreePath path = subtreeRootPath;
        // A collection the budget cut short is still hashable: Collect adds a node only after all its dirty children.
        Collect(subtreeRoot, ref path, pending, maxCollectedNodes);
        if (pending.Count < MinimumDirtyNodes) return;

        Span<PendingNode> nodes = pending.AsSpan();
        using ArrayPoolList<int> order = new(nodes.Length, nodes.Length);
        Span<int> orderSpan = order.AsSpan();
        BuildDeepestFirstOrder(nodes, orderSpan);

        // The kernel width is fixed by the hardware, so a group must never grow past it.
        int widestBatch = Avx512F.IsSupported ? HashBatchSize : Avx2HashBatchSize;
        Unsafe.SkipInit(out HashBuffer buffer);
        Span<byte> storage = MemoryMarshal.AsBytes((Span<Vector256<byte>>)buffer);
        TrieNode[] byClass = new TrieNode[MaxPaddedClass * HashBatchSize];
        Span<int> classCount = stackalloc int[MaxPaddedClass + 1];
        classCount.Clear();

        // Path length 0 is the trie's own root, which the caller hashes.
        int shallowest = Math.Max(subtreeRootPath.Length, 1);
        int currentPathLength = -1;
        foreach (int index in orderSpan)
        {
            ref readonly PendingNode pendingNode = ref nodes[index];
            int pathLength = pendingNode.Path.Length;
            if (pathLength < shallowest) break;
            if (pathLength != currentPathLength)
            {
                // A level's leftovers cannot wait for the next one, whose nodes are their parents.
                HashRemainingGroups(byClass, classCount, storage);
                currentPathLength = pathLength;
            }

            TrieNode node = pendingNode.Node;
            TreePath nodePath = pendingNode.Path;
            CappedArray<byte> rlp = node.PrepareRlp(resolver, ref nodePath, pool, canBeParallel: false);
            if (rlp.Length < Hash256.Size || rlp.Length >= KeccakHash.MaxBatchablePaddedLength)
            {
                // Below a hash the node is embedded in its parent; past the buffer it is hashed alone.
                node.ResolvePreparedKey();
                continue;
            }

            int paddedClass = PaddedClass(rlp.Length);
            int start = (paddedClass - 1) * HashBatchSize;
            byClass[start + classCount[paddedClass]] = node;
            if (++classCount[paddedClass] == widestBatch)
            {
                HashGroup(byClass.AsSpan(start, widestBatch), paddedClass, storage);
                classCount[paddedClass] = 0;
            }
        }

        HashRemainingGroups(byClass, classCount, storage);
    }

    private static void HashRemainingGroups(TrieNode[] byClass, Span<int> classCount, Span<byte> storage)
    {
        for (int paddedClass = 1; paddedClass <= MaxPaddedClass; paddedClass++)
        {
            int count = classCount[paddedClass];
            if (count == 0) continue;
            HashGroup(byClass.AsSpan((paddedClass - 1) * HashBatchSize, count), paddedClass, storage);
            classCount[paddedClass] = 0;
        }
    }

    /// <summary>Orders the collected nodes by path length, deepest first.</summary>
    /// <remarks>Two nodes at one path length are never each other's ancestor, so a whole level can be
    /// encoded and then hashed together, and deepest first is what leaves a node's children already
    /// hashed by the time it is encoded. Getting this backwards does not produce a wrong digest,
    /// because encoding a parent falls back to hashing its children itself; it only gives up the
    /// batching. No digest check can hold it, which is what <see cref="DeepestFirstPathLengths" />
    /// is for.</remarks>
    private static void BuildDeepestFirstOrder(ReadOnlySpan<PendingNode> nodes, Span<int> order)
    {
        Span<int> levelStart = stackalloc int[MaxPathLength + 1];
        levelStart.Clear();
        foreach (ref readonly PendingNode node in nodes)
        {
            levelStart[node.Path.Length]++;
        }

        int running = 0;
        for (int pathLength = MaxPathLength; pathLength >= 0; pathLength--)
        {
            int count = levelStart[pathLength];
            levelStart[pathLength] = running;
            running += count;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            order[levelStart[nodes[i].Path.Length]++] = i;
        }
    }

    /// <summary>The path lengths the level order visits, in the order it visits them.</summary>
    /// <remarks>Exists so a test can hold the ordering, which no digest check can - see
    /// <see cref="BuildDeepestFirstOrder" />.</remarks>
    internal static int[] DeepestFirstPathLengths(TrieNode subtreeRoot)
    {
        using ArrayPoolList<PendingNode> pending = new(64);
        TreePath path = TreePath.Empty;
        Collect(subtreeRoot, ref path, pending, MaxCollectedNodes);

        Span<PendingNode> nodes = pending.AsSpan();
        int[] order = new int[nodes.Length];
        BuildDeepestFirstOrder(nodes, order);

        int[] pathLengths = new int[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            pathLengths[i] = nodes[order[i]].Path.Length;
        }

        return pathLengths;
    }

    /// <summary>Rate blocks the Keccak padding rounds a message of this length up to.</summary>
    private static int PaddedClass(int length) => length / KeccakHash.RateBlockLength + 1;

    /// <returns>Whether the subtree fit within the collection budget.</returns>
    private static bool Collect(TrieNode node, ref TreePath path, ArrayPoolList<PendingNode> into, int maxCollectedNodes)
    {
        if (into.Count >= maxCollectedNodes) return false;

        if (node.IsBranch)
        {
            path.AppendMut(0);
            for (int i = 0; i < BranchChildCount; i++)
            {
                if (node.TryGetDirtyChild(i, out TrieNode? child) && child.Keccak is null)
                {
                    path.SetLast(i);
                    if (!Collect(child, ref path, into, maxCollectedNodes)) return false;
                }
            }

            path.TruncateOne();
        }
        else if (node.IsExtension && node.TryGetDirtyChild(0, out TrieNode? extensionChild) && extensionChild.Keccak is null)
        {
            int previousLength = node.AppendChildPath(ref path, 0);
            if (!Collect(extensionChild, ref path, into, maxCollectedNodes)) return false;
            path.TruncateMut(previousLength);
        }

        into.Add(new PendingNode(node, in path));
        return true;
    }

    /// <summary>Hashes nodes that share a padded length, or hashes a lone one on its own.</summary>
    private static void HashGroup(Span<TrieNode> group, int paddedClass, Span<byte> storage)
    {
        int minimumBatchCount = Avx512F.IsSupported ? MinimumAvx512BatchCount : Avx2HashBatchSize;
        if (group.Length < minimumBatchCount)
        {
            foreach (TrieNode node in group) node.ResolvePreparedKey();
            return;
        }

        // The narrowest kernel that covers the group, so a small one does not permute eight lanes.
        int batchSize = Avx512F.IsSupported && group.Length > Avx2HashBatchSize ? HashBatchSize : Avx2HashBatchSize;
        if (group.Length > batchSize) ThrowGroupWiderThanKernel();

        int paddedLength = paddedClass * KeccakHash.RateBlockLength;
        Span<byte> inputs = storage[..(batchSize * paddedLength)];
        Span<byte> hashes = storage.Slice(batchSize * paddedLength, batchSize * Hash256.Size);
        inputs.Clear();
        for (int i = 0; i < group.Length; i++)
        {
            ReadOnlySpan<byte> rlp = group[i].FullRlp.AsSpan();
            if (PaddedClass(rlp.Length) != paddedClass) ThrowUnexpectedLength();
            Span<byte> input = inputs.Slice(i * paddedLength, paddedLength);
            rlp.CopyTo(input);
            // Both padding bytes land on the same byte at the maximum length, so they merge.
            input[rlp.Length] |= 0x01;
            input[^1] |= 0x80;
        }

        // The kernel runs at its native width; lanes past the group stay cleared and are dropped.
        if (paddedLength == KeccakHash.RateBlockLength)
        {
            if (batchSize == HashBatchSize)
                KeccakHash.ComputePaddedBlocks8Avx512(ref inputs[0], ref hashes[0]);
            else
                KeccakHash.ComputePaddedBlocks4Avx2(ref inputs[0], ref hashes[0]);
        }
        else if (batchSize == HashBatchSize)
        {
            KeccakHash.ComputePaddedMultiBlocks8Avx512(ref inputs[0], paddedLength, ref hashes[0]);
        }
        else
        {
            KeccakHash.ComputePaddedMultiBlocks4Avx2(ref inputs[0], paddedLength, ref hashes[0]);
        }

        for (int i = 0; i < group.Length; i++)
        {
            ValueHash256 hash = new(hashes.Slice(i * Hash256.Size, Hash256.Size));
            group[i].SetPreparedKey(in hash);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnexpectedLength() =>
        throw new TrieException("A collected node changed length before batched hashing.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowGroupWiderThanKernel() =>
        throw new TrieException("More nodes were grouped than the batch kernel has lanes.");
}
