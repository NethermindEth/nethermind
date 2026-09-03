// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Atomically applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static class TrieUpdater
{
    private static readonly PbtNodePath RootPath = new([], 0);

    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Effective mutations are folded through the tree as traversal-local partitioned ranges, so mutations
    /// sharing a path share one traversal. All leaf and node writes are staged and handed to
    /// <see cref="IPbtStore.Apply"/> only after the complete mutation succeeds.
    /// </remarks>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes) =>
        UpdateRoot(store, currentRoot, changes, null);

    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtWriteBatch changes,
        TrieUpdaterMetrics? metrics)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) return currentRoot;

        // PbtWriteBatch supplies unique keys with deletions preceding writes.
        PbtWriteOperation[] operations = [.. changes.Operations];
        return FoldMutationsAtBoundary(store, metrics, RootPath, operations.AsSpan(), allowAbsent: true);
    }

    private static ValueHash256 FoldMutations(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader activeGroup,
        PbtNodePath path,
        Span<PbtWriteOperation> operations)
    {
        if (activeGroup.TryGetPosition(path, out _))
            return FoldMutationsInGroup(store, metrics, ref activeGroup, path, operations);

        return FoldMutationsAtBoundary(store, metrics, path, operations);
    }

    private static ValueHash256 FoldMutationsAtBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        PbtNodePath path,
        Span<PbtWriteOperation> operations,
        bool allowAbsent = false)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementPhysicalGroupFetches();
        RefCountingMemory? lease = store.GetNodeGroup(location.GroupKey);
        try
        {
            GroupFrameReader group = lease is null
                ? new(store, location.GroupKey, metrics)
                : new(store, location.GroupKey, metrics, lease.GetSpan());
            if (operations.Length > 1)
                BucketizeByGroupBoundary(operations, group.BitDepth);
            return FoldMutationsInGroup(store, metrics, ref group, path, operations, allowAbsent);
        }
        finally
        {
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static ValueHash256 FoldMutationsInGroup(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader group,
        PbtNodePath path,
        Span<PbtWriteOperation> operations,
        bool allowAbsent = false)
    {
        PbtNode? currentNode = allowAbsent ? group.TryLoad(path) : group.Load(path);
        if (currentNode is null)
        {
            int setCount = RetainSets(operations);
            return setCount == 0 ? default : BuildSubtreeInGroup(store, metrics, ref group, path, operations[..setCount]);
        }

        PbtNode current = currentNode;
        if (current is PbtLeafNode leaf)
        {
            PbtNode? surviving = leaf;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (!operation.Key.Equals(leaf.Key)) continue;
                if (operation.Kind == PbtWriteOperationKind.Delete)
                {
                    store.SetLeaf(leaf.Key, null);
                    surviving = null;
                }
                else
                {
                    store.SetLeaf(operation.Key, operation.Value);
                    surviving = new PbtLeafNode(operation.Key, operation.Value);
                }
                break;
            }

            int setCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (operation.Kind == PbtWriteOperationKind.Delete || operation.Key.Equals(leaf.Key)) continue;
                (operations[setCount], operations[index]) = (operations[index], operations[setCount]);
                setCount++;
            }

            if (surviving is null)
            {
                Remove(ref group, path);
                return setCount == 0 ? default : BuildSubtree(store, metrics, ref group, path, operations[..setCount]);
            }
            if (setCount == 0)
            {
                if (surviving.Hash != leaf.Hash) Store(ref group, path, surviving);
                return surviving.Hash;
            }
            return InsertSetsIntoNode(store, metrics, ref group, path, surviving, operations[..setCount]);
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        int matchingCount = FindMatchingBranchRange(branch, path.BitDepth, operations);
        Span<PbtWriteOperation> matchingOperations = operations[..matchingCount];
        int partition = PartitionByBit(matchingOperations, path.BitDepth + branch.Prefix.BitCount);
        PbtNodePath leftPath = path.Append(branch.Prefix, 0);
        PbtNodePath rightPath = path.Append(branch.Prefix, 1);
        ValueHash256 leftHash = branch.LeftHash;
        ValueHash256 rightHash = branch.RightHash;
        if (partition > 0)
            leftHash = FoldMutations(store, metrics, ref group, leftPath, matchingOperations[..partition]);
        if (partition < matchingOperations.Length)
            rightHash = FoldMutations(store, metrics, ref group, rightPath, matchingOperations[partition..]);

        ValueHash256 reconciledHash = CanonicalizeBranch(store, metrics, ref group, path, branch.Prefix, leftPath, leftHash, rightPath, rightHash);
        int divergentSetCount = RetainSets(operations[matchingCount..]);
        Span<PbtWriteOperation> divergentSets = operations.Slice(matchingCount, divergentSetCount);
        if (divergentSets.Length == 0) return reconciledHash;
        if (reconciledHash == default)
            return BuildSubtree(store, metrics, ref group, path, divergentSets);
        PbtNode reconciled = group.Load(path);
        return InsertSetsIntoNode(store, metrics, ref group, path, reconciled, divergentSets);
    }

    private static int RetainSets(Span<PbtWriteOperation> operations)
    {
        int setCount = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index].Kind == PbtWriteOperationKind.Delete) continue;
            (operations[setCount], operations[index]) = (operations[index], operations[setCount]);
            setCount++;
        }
        return setCount;
    }

    private static ValueHash256 CanonicalizeBranch(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader group,
        PbtNodePath path,
        PbtBitPrefix prefix,
        PbtNodePath leftPath,
        in ValueHash256 leftHash,
        PbtNodePath rightPath,
        in ValueHash256 rightHash)
    {
        if (leftHash != default && rightHash != default)
        {
            PbtBranchNode replacement = new(prefix, leftHash, rightHash);
            Store(ref group, path, replacement);
            return replacement.Hash;
        }
        if (leftHash == default && rightHash == default)
        {
            Remove(ref group, path);
            return default;
        }

        int remainingDirection = leftHash != default ? 0 : 1;
        PbtNodePath remainingPath = remainingDirection == 0 ? leftPath : rightPath;
        if (group.TryGetPosition(remainingPath, out _))
            return PromoteRemainingNode(ref group, ref group, path, prefix, remainingPath, remainingDirection);

        return PromoteRemainingNodeAtBoundary(store, metrics, ref group, path, prefix, remainingPath, remainingDirection);
    }

    private static ValueHash256 PromoteRemainingNodeAtBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader parentGroup,
        PbtNodePath path,
        PbtBitPrefix prefix,
        PbtNodePath remainingPath,
        int remainingDirection)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(remainingPath);
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementPhysicalGroupFetches();
        RefCountingMemory? lease = store.GetNodeGroup(location.GroupKey);
        try
        {
            GroupFrameReader remainingGroup = lease is null
                ? new(store, location.GroupKey, metrics)
                : new(store, location.GroupKey, metrics, lease.GetSpan());
            return PromoteRemainingNode(ref parentGroup, ref remainingGroup, path, prefix, remainingPath, remainingDirection);
        }
        finally
        {
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static ValueHash256 PromoteRemainingNode(
        scoped ref GroupFrameReader parentGroup,
        scoped ref GroupFrameReader remainingGroup,
        PbtNodePath path,
        PbtBitPrefix prefix,
        PbtNodePath remainingPath,
        int remainingDirection)
    {
        PbtNode remaining = remainingGroup.Load(remainingPath);
        Remove(ref remainingGroup, remainingPath);
        PbtNode promoted = remaining is PbtBranchNode remainingBranch
            ? new PbtBranchNode(PbtBitPrefix.Concat(prefix, remainingDirection, remainingBranch.Prefix), remainingBranch.LeftHash, remainingBranch.RightHash)
            : remaining;
        Store(ref parentGroup, path, promoted);
        return promoted.Hash;
    }

    private static ValueHash256 InsertSets(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader activeGroup,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (activeGroup.TryGetPosition(path, out _))
            return InsertSetsInGroup(store, metrics, ref activeGroup, path, sets);

        return InsertSetsAtBoundary(store, metrics, path, sets);
    }

    private static ValueHash256 InsertSetsAtBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementPhysicalGroupFetches();
        RefCountingMemory? lease = store.GetNodeGroup(location.GroupKey);
        try
        {
            GroupFrameReader group = lease is null
                ? new(store, location.GroupKey, metrics)
                : new(store, location.GroupKey, metrics, lease.GetSpan());
            if (sets.Length > 1)
                BucketizeByGroupBoundary(sets, group.BitDepth);
            return InsertSetsInGroup(store, metrics, ref group, path, sets);
        }
        finally
        {
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static ValueHash256 InsertSetsInGroup(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader group,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        PbtNode current = group.Load(path);
        return InsertSetsIntoNode(store, metrics, ref group, path, current, sets);
    }

    private static ValueHash256 InsertSetsIntoNode(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader activeGroup,
        PbtNodePath path,
        PbtNode current,
        Span<PbtWriteOperation> sets)
    {
        if (current is PbtLeafNode leaf)
            return InsertSetsIntoLeaf(store, metrics, ref activeGroup, path, leaf, sets);
        if (current is PbtBranchNode branch)
            return InsertSetsIntoBranch(store, metrics, ref activeGroup, path, branch, sets);
        throw new ArgumentOutOfRangeException(nameof(current));
    }

    private static ValueHash256 InsertSetsIntoLeaf(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader group,
        PbtNodePath path,
        PbtLeafNode leaf,
        Span<PbtWriteOperation> sets)
    {
        int differingBit = int.MaxValue;
        for (int index = 0; index < sets.Length; index++)
        {
            PbtFullKey key = sets[index].Key;
            if (leaf.Key.Equals(key)) continue;
            int difference = leaf.Key.FirstDifferingBit(key, path.BitDepth);
            if (difference == Math.Min(leaf.Key.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
            differingBit = Math.Min(differingBit, difference);
        }

        if (differingBit == int.MaxValue)
        {
            store.SetLeaf(sets[0].Key, sets[0].Value);
            PbtLeafNode replacement = new(sets[0].Key, sets[0].Value);
            Store(ref group, path, replacement);
            return replacement.Hash;
        }

        PbtBitPrefix common = PbtBitPrefix.FromKey(leaf.Key, path.BitDepth, differingBit - path.BitDepth);
        int existingDirection = leaf.Key.GetBit(differingBit);
        int partition = PartitionByBit(sets, differingBit);
        PbtNodePath leftPath = path.Append(common, 0);
        PbtNodePath rightPath = path.Append(common, 1);
        PbtNodePath existingPath = existingDirection == 0 ? leftPath : rightPath;
        if (group.TryGetPosition(existingPath, out _))
        {
            Store(ref group, existingPath, leaf);
            return CompleteLeafSplit(store, metrics, ref group, ref group, path, common, leaf, sets, existingDirection, partition, leftPath, rightPath);
        }

        return CompleteLeafSplitAtBoundary(store, metrics, ref group, path, common, leaf, sets, existingDirection, partition, leftPath, rightPath, existingPath);
    }

    private static ValueHash256 CompleteLeafSplitAtBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader parentGroup,
        PbtNodePath path,
        PbtBitPrefix common,
        PbtLeafNode leaf,
        Span<PbtWriteOperation> sets,
        int existingDirection,
        int partition,
        PbtNodePath leftPath,
        PbtNodePath rightPath,
        PbtNodePath existingPath)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(existingPath);
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementPhysicalGroupFetches();
        RefCountingMemory? lease = store.GetNodeGroup(location.GroupKey);
        try
        {
            GroupFrameReader existingGroup = lease is null
                ? new(store, location.GroupKey, metrics)
                : new(store, location.GroupKey, metrics, lease.GetSpan());
            existingGroup.Store(location.Position, leaf);
            return CompleteLeafSplit(store, metrics, ref parentGroup, ref existingGroup, path, common, leaf, sets, existingDirection, partition, leftPath, rightPath);
        }
        finally
        {
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static ValueHash256 CompleteLeafSplit(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader parentGroup,
        scoped ref GroupFrameReader existingGroup,
        PbtNodePath path,
        PbtBitPrefix common,
        PbtLeafNode leaf,
        Span<PbtWriteOperation> sets,
        int existingDirection,
        int partition,
        PbtNodePath leftPath,
        PbtNodePath rightPath)
    {
        ValueHash256 leftHash;
        ValueHash256 rightHash;
        if (existingDirection == 0)
        {
            leftHash = partition > 0
                ? InsertSetsIntoNode(store, metrics, ref existingGroup, leftPath, leaf, sets[..partition])
                : leaf.Hash;
            rightHash = BuildSubtree(store, metrics, ref parentGroup, rightPath, sets[partition..]);
        }
        else
        {
            leftHash = BuildSubtree(store, metrics, ref parentGroup, leftPath, sets[..partition]);
            rightHash = partition < sets.Length
                ? InsertSetsIntoNode(store, metrics, ref existingGroup, rightPath, leaf, sets[partition..])
                : leaf.Hash;
        }

        PbtBranchNode split = new(common, leftHash, rightHash);
        Store(ref parentGroup, path, split);
        return split.Hash;
    }

    private static ValueHash256 InsertSetsIntoBranch(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader group,
        PbtNodePath path,
        PbtBranchNode branch,
        Span<PbtWriteOperation> sets)
    {
        int firstMismatch = branch.Prefix.BitCount;
        for (int index = 0; index < sets.Length; index++)
        {
            PbtFullKey key = sets[index].Key;
            int available = key.BitLength - path.BitDepth;
            int matched = MatchingPrefixBits(branch.Prefix, key, path.BitDepth);
            if (matched == available)
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
            firstMismatch = Math.Min(firstMismatch, matched);
        }

        if (firstMismatch < branch.Prefix.BitCount)
        {
            PbtBitPrefix common = PrefixSlice(branch.Prefix, 0, firstMismatch);
            int existingDirection = branch.Prefix.GetBit(firstMismatch);
            PbtBranchNode relocated = new(
                PrefixSlice(branch.Prefix, firstMismatch + 1, branch.Prefix.BitCount - firstMismatch - 1),
                branch.LeftHash,
                branch.RightHash);
            int directionBit = path.BitDepth + firstMismatch;
            int partition = PartitionByBit(sets, directionBit);
            PbtNodePath leftPath = path.Append(common, 0);
            PbtNodePath rightPath = path.Append(common, 1);
            PbtNodePath existingPath = existingDirection == 0 ? leftPath : rightPath;
            if (group.TryGetPosition(existingPath, out _))
            {
                Store(ref group, existingPath, relocated);
                return CompleteBranchSplit(store, metrics, ref group, ref group, path, common, relocated, sets, existingDirection, partition, leftPath, rightPath);
            }

            return CompleteBranchSplitAtBoundary(store, metrics, ref group, path, common, relocated, sets, existingDirection, partition, leftPath, rightPath, existingPath);
        }

        int childDirectionBit = path.BitDepth + branch.Prefix.BitCount;
        int childPartition = PartitionByBit(sets, childDirectionBit);
        ValueHash256 replacementLeft = branch.LeftHash;
        ValueHash256 replacementRight = branch.RightHash;
        if (childPartition > 0)
        {
            PbtNodePath leftPath = path.Append(branch.Prefix, 0);
            replacementLeft = InsertSets(store, metrics, ref group, leftPath, sets[..childPartition]);
        }
        if (childPartition < sets.Length)
        {
            PbtNodePath rightPath = path.Append(branch.Prefix, 1);
            replacementRight = InsertSets(store, metrics, ref group, rightPath, sets[childPartition..]);
        }

        PbtBranchNode replacement = new(branch.Prefix, replacementLeft, replacementRight);
        Store(ref group, path, replacement);
        return replacement.Hash;
    }

    private static ValueHash256 CompleteBranchSplitAtBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader parentGroup,
        PbtNodePath path,
        PbtBitPrefix common,
        PbtBranchNode relocated,
        Span<PbtWriteOperation> sets,
        int existingDirection,
        int partition,
        PbtNodePath leftPath,
        PbtNodePath rightPath,
        PbtNodePath existingPath)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(existingPath);
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementPhysicalGroupFetches();
        RefCountingMemory? lease = store.GetNodeGroup(location.GroupKey);
        try
        {
            GroupFrameReader existingGroup = lease is null
                ? new(store, location.GroupKey, metrics)
                : new(store, location.GroupKey, metrics, lease.GetSpan());
            existingGroup.Store(location.Position, relocated);
            return CompleteBranchSplit(store, metrics, ref parentGroup, ref existingGroup, path, common, relocated, sets, existingDirection, partition, leftPath, rightPath);
        }
        finally
        {
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static ValueHash256 CompleteBranchSplit(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader parentGroup,
        scoped ref GroupFrameReader existingGroup,
        PbtNodePath path,
        PbtBitPrefix common,
        PbtBranchNode relocated,
        Span<PbtWriteOperation> sets,
        int existingDirection,
        int partition,
        PbtNodePath leftPath,
        PbtNodePath rightPath)
    {
        ValueHash256 leftHash;
        ValueHash256 rightHash;
        if (existingDirection == 0)
        {
            leftHash = partition > 0
                ? InsertSetsIntoNode(store, metrics, ref existingGroup, leftPath, relocated, sets[..partition])
                : relocated.Hash;
            rightHash = BuildSubtree(store, metrics, ref parentGroup, rightPath, sets[partition..]);
        }
        else
        {
            leftHash = BuildSubtree(store, metrics, ref parentGroup, leftPath, sets[..partition]);
            rightHash = partition < sets.Length
                ? InsertSetsIntoNode(store, metrics, ref existingGroup, rightPath, relocated, sets[partition..])
                : relocated.Hash;
        }

        PbtBranchNode split = new(common, leftHash, rightHash);
        Store(ref parentGroup, path, split);
        return split.Hash;
    }

    private static ValueHash256 BuildSubtree(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader activeGroup,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (activeGroup.TryGetPosition(path, out _))
            return BuildSubtreeInGroup(store, metrics, ref activeGroup, path, sets);

        return BuildSubtreeAtBoundary(store, metrics, path, sets);
    }

    private static ValueHash256 BuildSubtreeAtBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementPhysicalGroupFetches();
        RefCountingMemory? lease = store.GetNodeGroup(location.GroupKey);
        try
        {
            GroupFrameReader group = lease is null
                ? new(store, location.GroupKey, metrics)
                : new(store, location.GroupKey, metrics, lease.GetSpan());
            if (sets.Length > 1)
                BucketizeByGroupBoundary(sets, group.BitDepth);
            return BuildSubtreeInGroup(store, metrics, ref group, path, sets);
        }
        finally
        {
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static ValueHash256 BuildSubtreeInGroup(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        scoped ref GroupFrameReader group,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (sets.Length == 1)
        {
            PbtWriteOperation operation = sets[0];
            store.SetLeaf(operation.Key, operation.Value);
            PbtLeafNode leaf = new(operation.Key, operation.Value);
            Store(ref group, path, leaf);
            return leaf.Hash;
        }

        PbtFullKey firstKey = sets[0].Key;
        int differingBit = int.MaxValue;
        for (int index = 1; index < sets.Length; index++)
        {
            PbtFullKey key = sets[index].Key;
            int difference = firstKey.FirstDifferingBit(key, path.BitDepth);
            if (difference == Math.Min(firstKey.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(sets));
            differingBit = Math.Min(differingBit, difference);
        }
        PbtBitPrefix prefix = PbtBitPrefix.FromKey(firstKey, path.BitDepth, differingBit - path.BitDepth);
        int partition = PartitionByBit(sets, differingBit);
        PbtNodePath leftPath = path.Append(prefix, 0);
        PbtNodePath rightPath = path.Append(prefix, 1);
        ValueHash256 leftHash = BuildSubtree(store, metrics, ref group, leftPath, sets[..partition]);
        ValueHash256 rightHash = BuildSubtree(store, metrics, ref group, rightPath, sets[partition..]);
        PbtBranchNode branch = new(prefix, leftHash, rightHash);
        Store(ref group, path, branch);
        return branch.Hash;
    }

    private static int FindMatchingBranchRange(
        PbtBranchNode branch,
        int bitDepth,
        Span<PbtWriteOperation> operations)
    {
        int matchingCount = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            PbtFullKey key = operations[index].Key;
            int available = key.BitLength - bitDepth;
            if (available <= branch.Prefix.BitCount ||
                MatchingPrefixBits(branch.Prefix, key, bitDepth) != branch.Prefix.BitCount)
                continue;

            (operations[matchingCount], operations[index]) = (operations[index], operations[matchingCount]);
            matchingCount++;
        }
        return matchingCount;
    }

    private static int PartitionByBit(Span<PbtWriteOperation> operations, int bitIndex)
    {
        int partition = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index].Key.GetBit(bitIndex) != 0) continue;
            (operations[partition], operations[index]) = (operations[index], operations[partition]);
            partition++;
        }
        return partition;
    }

    private static void BucketizeByGroupBoundary(Span<PbtWriteOperation> operations, int groupDepth)
    {
        Span<int> starts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        Span<int> counts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        for (int index = 0; index < operations.Length; index++)
            counts[BoundarySlot(operations[index].Key, groupDepth)]++;

        int total = 0;
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
        {
            starts[bucket] = total;
            total += counts[bucket];
        }

        Span<int> next = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        starts.CopyTo(next);
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
        {
            int end = starts[bucket] + counts[bucket];
            while (next[bucket] < end)
            {
                int index = next[bucket];
                int destination = BoundarySlot(operations[index].Key, groupDepth);
                if (destination == bucket)
                {
                    next[bucket]++;
                    continue;
                }

                (operations[index], operations[next[destination]]) =
                    (operations[next[destination]], operations[index]);
                next[destination]++;
            }
        }
    }

    private static int BoundarySlot(PbtFullKey key, int groupDepth)
    {
        byte value = key.Bytes[groupDepth >> 3];
        return (value >> (4 - (groupDepth & 4))) & 0x0F;
    }

    private static int MatchingPrefixBits(PbtBitPrefix prefix, PbtFullKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        int index = 0;
        while (index < count && prefix.GetBit(index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }

    private static PbtBitPrefix PrefixSlice(PbtBitPrefix prefix, int start, int count)
    {
        byte[] bytes = new byte[PbtBitPrefix.ByteCount(count)];
        for (int index = 0; index < count; index++)
        {
            if (prefix.GetBit(start + index) != 0) bytes[index >> 3] |= (byte)(1 << (7 - (index & 7)));
        }
        return PbtBitPrefix.TakeOwnership(bytes, count);
    }

    private static void Store(scoped ref GroupFrameReader group, PbtNodePath path, PbtNode node)
    {
        if (!group.TryGetPosition(path, out int position))
            throw new InvalidOperationException("The PBT node does not belong to the active group.");
        group.Store(position, node);
    }

    private static void Remove(scoped ref GroupFrameReader group, PbtNodePath path)
    {
        if (!group.TryGetPosition(path, out int position))
            throw new InvalidOperationException("The PBT node does not belong to the active group.");
        group.Remove(position);
    }

    private ref struct GroupFrameReader
    {
        private readonly IPbtStore _store;
        private readonly PbtNodePath _groupKey;
        private readonly TrieUpdaterMetrics? _metrics;
        private readonly ReadOnlySpan<byte> _payload;
        private readonly FrameStorage _storage;

        internal GroupFrameReader(
            IPbtStore store,
            PbtNodePath groupKey,
            TrieUpdaterMetrics? metrics)
        {
            _store = store;
            _groupKey = groupKey;
            _metrics = metrics;
            _payload = default;
            _storage = new();
        }

        internal GroupFrameReader(
            IPbtStore store,
            PbtNodePath groupKey,
            TrieUpdaterMetrics? metrics,
            Span<byte> payload)
            : this(store, groupKey, metrics)
        {
            _payload = payload;
            metrics?.IncrementGroupParses();
            PbtNodeGroupReader reader = new(groupKey, payload);
            for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
            {
                if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                if (!reader.TryGetNodeRange(position, out int offset, out int length)) continue;

                _storage.Offsets[position] = offset;
                _storage.Lengths[position] = length;
                _storage.States[position] = SlotState.Persisted;
            }
        }

        internal int BitDepth => _groupKey.BitDepth;

        internal bool TryGetPosition(PbtNodePath path, out int position)
        {
            if (PbtFourLevelGroupGeometry.GroupDepthOf(path.BitDepth) != _groupKey.BitDepth
                || !StartsWith(path.Path, _groupKey.Path, _groupKey.BitDepth))
            {
                position = 0;
                return false;
            }

            position = PbtFourLevelGroupGeometry.PositionOf(path);
            return true;
        }

        internal PbtNode? TryLoad(PbtNodePath path)
        {
            TryGetPosition(path, out int position);
            return _storage.States[position] switch
            {
                SlotState.Absent => null,
                SlotState.Persisted => _storage.Nodes[position] ??= PbtNodeCodec.Decode(
                    _payload.Slice(_storage.Offsets[position], _storage.Lengths[position])),
                SlotState.Staged => _storage.Nodes[position],
                SlotState.Tombstone => throw new InvalidDataException("A referenced PBT node is missing."),
                _ => throw new ArgumentOutOfRangeException(nameof(path)),
            };
        }

        internal PbtNode Load(PbtNodePath path)
        {
            TryGetPosition(path, out int position);
            return _storage.States[position] switch
            {
                SlotState.Persisted => _storage.Nodes[position] ??= PbtNodeCodec.Decode(
                    _payload.Slice(_storage.Offsets[position], _storage.Lengths[position])),
                SlotState.Staged => _storage.Nodes[position]!,
                _ => throw new InvalidDataException("A referenced PBT node is missing."),
            };
        }

        internal void Store(int position, PbtNode node)
        {
            byte[] encoding = PbtNodeCodec.Encode(node);
            if (_storage.States[position] == SlotState.Persisted && PersistedEncodingEquals(position, encoding))
            {
                _storage.Nodes[position] = node;
                return;
            }

            _storage.States[position] = SlotState.Staged;
            _storage.Nodes[position] = node;
            _store.SetNode(PbtFourLevelGroupGeometry.PathOf(_groupKey, position), encoding);
            _metrics?.AddEmittedNodeWrites(1);
        }

        internal void Remove(int position)
        {
            if (_storage.Lengths[position] != 0)
            {
                _storage.States[position] = SlotState.Tombstone;
                _store.SetNode(PbtFourLevelGroupGeometry.PathOf(_groupKey, position), null);
                _metrics?.AddEmittedNodeWrites(1);
            }
            else
            {
                _storage.States[position] = SlotState.Absent;
            }
            _storage.Nodes[position] = null;
        }

        private bool PersistedEncodingEquals(int position, ReadOnlySpan<byte> encoding) =>
            _storage.Lengths[position] != 0
            && _payload.Slice(_storage.Offsets[position], _storage.Lengths[position]).SequenceEqual(encoding);

        private static bool StartsWith(ReadOnlySpan<byte> path, ReadOnlySpan<byte> prefix, int prefixBitCount)
        {
            int completeBytes = prefixBitCount >> 3;
            if (!path[..completeBytes].SequenceEqual(prefix[..completeBytes])) return false;
            int remainingBits = prefixBitCount & 7;
            return remainingBits == 0
                || ((path[completeBytes] ^ prefix[completeBytes]) & (0xFF << (8 - remainingBits))) == 0;
        }

        private sealed class FrameStorage
        {
            internal OffsetBuffer Offsets;
            internal LengthBuffer Lengths;
            internal NodeBuffer Nodes;
            internal StateBuffer States;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct OffsetBuffer
        {
            private int _element;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct LengthBuffer
        {
            private int _element;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct NodeBuffer
        {
            private PbtNode? _element;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct StateBuffer
        {
            private SlotState _element;
        }

        private enum SlotState : byte
        {
            Absent,
            Persisted,
            Staged,
            Tombstone,
        }
    }
}

internal sealed class TrieUpdaterMetrics
{
    internal int PhysicalGroupFetches { get; private set; }
    internal int GroupParses { get; private set; }
    internal int GroupFrameResolutions { get; private set; }
    internal int EmittedNodeWrites { get; private set; }

    internal void IncrementPhysicalGroupFetches() => PhysicalGroupFetches++;
    internal void IncrementGroupParses() => GroupParses++;
    internal void IncrementGroupFrameResolutions() => GroupFrameResolutions++;
    internal void AddEmittedNodeWrites(int count) => EmittedNodeWrites += count;
}
