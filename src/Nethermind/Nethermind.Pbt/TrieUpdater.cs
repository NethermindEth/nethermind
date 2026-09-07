// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static class TrieUpdater
{
    private const int FullSortThreshold = PbtFourLevelGroupGeometry.BoundarySlots;
    private static readonly PbtNodePath RootPath = new([], 0);

    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Effective mutations are folded through the tree as traversal-local partitioned ranges, so mutations
    /// sharing a path share one traversal. Each completed frame publishes its complete node group.
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

        PbtWriteOperation[] operations = [.. changes.Operations];
        return UpdateRoot(store, currentRoot, operations, default, metrics);
    }

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet changes, TrieUpdaterMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        changes.Consume(out PbtWriteOperation[] operations, out int[] precalculated);
        return UpdateRoot(store, currentRoot, operations, new(precalculated, 0, 0, false, false), metrics);
    }

    /// <summary>Folds disjoint partitions concurrently before merging their shared ancestors.</summary>
    /// <remarks>
    /// The supplied store must support concurrent reads and writes. Failed folds may leave partial writes;
    /// the caller owns failure isolation and must not reuse that state without recovery.
    /// </remarks>
    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        IReadOnlyDictionary<PbtPartition, PbtPartitionWriteBatch> changes,
        TrieUpdaterMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        List<PartitionFold> workers = new(3);
        GroupMutationFrame?[] sharedGroups = new GroupMutationFrame[16];
        Subtree[][] zoneBoundaries = new Subtree[16][];
        Subtree[] rootBoundaries = new Subtree[16];
        try
        {
            foreach ((PbtPartition partition, PbtPartitionWriteBatch batch) in changes)
            {
                byte zone = partition switch
                {
                    PbtPartition.Account => Eip8297KeyDerivation.AccountZone,
                    PbtPartition.Code => Eip8297KeyDerivation.CodeZone,
                    PbtPartition.Storage => Eip8297KeyDerivation.StorageZone,
                    _ => throw new ArgumentOutOfRangeException(nameof(changes)),
                };
                batch.Consume(out PbtWriteOperation[] operations, out int[] table);
                if (operations.Length != 0) workers.Add(new(store, zone, operations, table, metrics is not null));
            }
            if (workers.Count == 0) return currentRoot;

            using GroupMutationFrame rootGroup = new(store, RootPath, metrics);
            Decompose(rootGroup, rootGroup.Take(RootPath, allowAbsent: true), 0, rootBoundaries);
            foreach (PartitionFold worker in workers)
            {
                int slot = worker.Zone >> 4;
                if (sharedGroups[slot] is not { } sharedGroup)
                {
                    sharedGroup = new(store, new PbtNodePath([(byte)(slot << 4)], 4), metrics);
                    sharedGroups[slot] = sharedGroup;
                    zoneBoundaries[slot] = new Subtree[16];
                    Decompose(sharedGroup, Resolve(rootGroup, rootBoundaries[slot]), 4, zoneBoundaries[slot]);
                }
                // Depth-eight roots still belong to the shared depth-four group. Resolve them before dispatch;
                // those frames retain their immutable leases until every worker and the ancestor merge finish.
                worker.Current = Resolve(sharedGroup, zoneBoundaries[slot][worker.Zone & 15]);
            }

            Parallel.ForEach(workers, new ParallelOptions { MaxDegreeOfParallelism = 3 }, static worker => worker.Fold());

            foreach (PartitionFold worker in workers)
            {
                zoneBoundaries[worker.Zone >> 4][worker.Zone & 15] = worker.Result;
                if (worker.Metrics is { } workerMetrics) metrics!.Add(workerMetrics);
            }
            for (int slot = 0; slot < sharedGroups.Length; slot++)
            {
                if (sharedGroups[slot] is not { } sharedGroup) continue;
                rootBoundaries[slot] = Compose(sharedGroup, zoneBoundaries[slot]);
                sharedGroup.Flush();
            }
            ValueHash256 hash = Place(rootGroup, Compose(rootGroup, rootBoundaries), PbtFourLevelGroupGeometry.RootPosition, 0);
            rootGroup.Flush();

            return hash;
        }
        finally
        {
            foreach (GroupMutationFrame? sharedGroup in sharedGroups) sharedGroup?.Dispose();
        }
    }

    private sealed class PartitionFold(IPbtStore store, byte zone, PbtWriteOperation[] operations, int[] table, bool collectMetrics)
    {
        internal byte Zone { get; } = zone;
        internal TrieUpdaterMetrics? Metrics { get; } = collectMetrics ? new() : null;
        internal Subtree Current { get; set; }
        internal Subtree Result { get; private set; }

        internal void Fold()
        {
            using GroupMutationFrame group = new(store, new PbtNodePath([Zone], 8), Metrics);
            // Consume the producer's nibble bounds before filtering deletes or comparing deeper key prefixes.
            Result = FoldBoundary(store, Metrics, group, Current, operations, new(table, 8, 8, false, false));
            group.Flush();
            Result = Result.PreserveBeyond(group);
        }
    }

    private static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, Span<PbtWriteOperation> operations, BucketPlan plan, TrieUpdaterMetrics? metrics)
    {
        if (operations.IsEmpty) return currentRoot;
        using GroupMutationFrame group = new(store, RootPath, metrics);
        Subtree root = group.Take(RootPath, allowAbsent: true);
        Subtree result = FoldMutations(store, metrics, group, root, operations, plan);
        ValueHash256 hash = Place(group, result, PbtFourLevelGroupGeometry.RootPosition, 0);
        group.Flush();
        return hash;
    }

    private static Subtree FoldMutations(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupMutationFrame ownerGroup,
        Subtree current,
        Span<PbtWriteOperation> operations,
        BucketPlan plan)
    {
        int depth = plan.Depth;
        current = Resolve(ownerGroup, current);
        if (operations.IsEmpty) return current;

        if (current.IsEmpty || current.Reader.IsLeaf)
        {
            bool hasLeaf = !current.IsEmpty;
            PbtFullKey leafKey = hasLeaf ? new(current.Reader.Key) : default;
            int setCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (hasLeaf && operation.Key.Equals(leafKey))
                {
                    if (operation.Kind == PbtWriteOperationKind.Delete)
                    {
                        current = default;
                    }
                    else
                    {
                        current = new(PbtNodeCodec.EncodeLeaf(operation.Key, operation.Value.Bytes), current.Path);
                    }
                }
                else if (operation.Kind == PbtWriteOperationKind.Set)
                {
                    (operations[setCount], operations[index]) = (operations[index], operations[setCount]);
                    setCount++;
                }
            }

            if (setCount != operations.Length) plan = plan.AfterFiltering(preservesOrder: true);
            operations = operations[..setCount];
            if (operations.IsEmpty) return current;
            if (current.IsEmpty && operations.Length == 1)
                return CreateLeaf(operations[0]);
        }
        else
        {
            // A shorter replacement can become valid when this same batch deletes the entire subtree.
            PbtWriteOperation? terminalSet = null;
            int remainingCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (operation.Key.BitLength == depth)
                {
                    if (operation.Kind == PbtWriteOperationKind.Set) terminalSet = operation;
                    continue;
                }
                (operations[remainingCount], operations[index]) = (operations[index], operations[remainingCount]);
                remainingCount++;
            }
            if (remainingCount != operations.Length)
            {
                Subtree remaining = FoldMutations(store, metrics, ownerGroup, current, operations[..remainingCount], plan.AfterFiltering(preservesOrder: true));
                if (terminalSet is not { } replacement) return remaining;
                if (!remaining.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                return CreateLeaf(replacement);
            }
        }

        plan = EstablishRangeKnowledge(current, operations, plan, metrics);
        int branchDepth = FindBranchDepth(current, operations[0].Key, plan);
        int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (groupDepth != depth)
        {
            if (!plan.Precalculated.IsEmpty && BitOperations.IsPow2(plan.Precalculated[0]))
            {
                metrics?.IncrementPrecalculatedLevels();
                return FoldMutations(store, metrics, ownerGroup, current, operations,
                    plan.ForChild(BitOperations.TrailingZeroCount(plan.Precalculated[0])));
            }

            // The range's prefix survives the jump; the existing subtree only limits how far we can jump.
            return FoldMutations(store, metrics, ownerGroup, current, operations, plan.AfterJump(groupDepth));
        }

        if (ownerGroup.BitDepth == depth)
            return FoldBoundary(store, metrics, ownerGroup, current, operations, plan);

        using GroupMutationFrame group = new(store, PbtNodePath.FromKey(operations[0].Key, depth), metrics);
        Subtree result = FoldBoundary(store, metrics, group, current, operations, plan);
        group.Flush();
        return result.PreserveBeyond(group);
    }

    private static Subtree FoldBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupMutationFrame group,
        Subtree current,
        Span<PbtWriteOperation> operations,
        BucketPlan plan)
    {
        int depth = plan.Depth;
        Span<int> offsets = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots + 1];
        PartitionOutcome partition = plan.BucketSort(operations, offsets, metrics);
        RefList16<Subtree> boundaryBuffer = new(PbtFourLevelGroupGeometry.BoundarySlots);
        Span<Subtree> boundaries = boundaryBuffer.AsSpan();
        Decompose(group, current, depth, boundaries);

        for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            Span<PbtWriteOperation> bucket = operations[offsets[slot]..offsets[slot + 1]];
            boundaries[slot] = FoldMutations(
                store, metrics, group, boundaries[slot], bucket, partition.Plan.ForChild(slot));
        }

        return Compose(group, boundaries);
    }

    private static Subtree Compose(GroupMutationFrame group, Span<Subtree> boundaries)
    {
        for (int level = PbtFourLevelGroupGeometry.LevelsPerGroup - 1; level >= 0; level--)
        {
            int width = 1 << (PbtFourLevelGroupGeometry.LevelsPerGroup - level);
            for (int slot = 0; slot < boundaries.Length; slot += width)
            {
                Subtree left = boundaries[slot];
                Subtree right = boundaries[slot + width / 2];
                if (left.IsEmpty)
                {
                    boundaries[slot] = right;
                    continue;
                }
                if (right.IsEmpty) continue;

                PbtNodePath branchPath = BoundaryPath(group.GroupKey, slot, level);
                // Each preceding leaf contributes two post-order positions, except its still-open ancestors.
                int position = 2 * (slot + width) - 2 - BitOperations.PopCount((uint)slot);
                ValueHash256 leftHash = Place(group, left, position - width, branchPath.BitDepth + 1);
                ValueHash256 rightHash = Place(group, right, position - 1, branchPath.BitDepth + 1);
                boundaries[slot] = new(PbtNodeCodec.EncodeBranch([], 0, leftHash, rightHash), branchPath);
            }
        }

        // The root is handed up unplaced. Its old slot belongs to this frame, which may exit before placement.
        return Resolve(group, boundaries[0]);
    }

    private static void Decompose(GroupMutationFrame group, Subtree current, int depth, Span<Subtree> boundaries)
    {
        if (current.IsEmpty) return;
        int boundaryDepth = depth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (current.Encoding.IsEmpty && current.Path!.BitDepth == boundaryDepth)
        {
            boundaries[BoundarySlot(current.Path.Path, depth)] = current;
            return;
        }

        current = Resolve(group, current);
        if (!current.IsEmpty && current.Reader.IsLeaf)
        {
            boundaries[BoundarySlot(current.Reader.Key, depth)] = current;
            return;
        }

        PbtNodeReader branch = current.Reader;
        int branchDepth = current.Path!.BitDepth + branch.PrefixBitCount;
        if (branchDepth >= boundaryDepth)
        {
            int slot = 0;
            for (int bit = depth; bit < boundaryDepth; bit++)
                slot = (slot << 1) | PrefixBit(current, bit);
            boundaries[slot] = current;
            return;
        }

        Decompose(group, new(branch.LeftHash, current.Path.Append(branch.Prefix, branch.PrefixBitCount, 0)), depth, boundaries);
        Decompose(group, new(branch.RightHash, current.Path.Append(branch.Prefix, branch.PrefixBitCount, 1)), depth, boundaries);
    }

    private static BucketPlan EstablishRangeKnowledge(Subtree current, Span<PbtWriteOperation> operations, BucketPlan plan, TrieUpdaterMetrics? metrics)
    {
        bool validatePrefixes = (current.IsEmpty || current.Reader.IsLeaf) && !plan.PrefixesValidated;
        if (!validatePrefixes && !plan.Precalculated.IsEmpty)
        {
            int mask = plan.Precalculated[0];
            int bound = BitOperations.IsPow2(mask) ? plan.Depth + PbtFourLevelGroupGeometry.LevelsPerGroup : plan.Depth;
            return plan.WithRangeKnowledge(Math.Max(plan.BranchDepth, bound), plan.PrefixesValidated);
        }
        if (plan.BranchDepth >= plan.Depth && plan.BranchDepth != 0 && !validatePrefixes) return plan;

        int branchDepth = FindOperationsBranchDepth(operations, plan.Depth, validatePrefixes, plan.IsSorted, metrics, out bool prefixesValidated);
        return plan.WithRangeKnowledge(branchDepth, plan.PrefixesValidated || prefixesValidated);
    }

    private static int FindBranchDepth(Subtree current, PbtFullKey firstKey, BucketPlan plan)
    {
        int branchDepth = plan.BranchDepth;
        if (!current.IsEmpty && current.Reader.IsLeaf)
        {
            PbtFullKey leafKey = new(current.Reader.Key);
            int difference = leafKey.FirstDifferingBit(firstKey, plan.Depth);
            if (difference == Math.Min(leafKey.BitLength, firstKey.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", "operations");
            branchDepth = Math.Min(branchDepth, difference);
        }
        else if (!current.IsEmpty)
        {
            int prefixStart = current.Path!.BitDepth;
            branchDepth = Math.Min(branchDepth, prefixStart + MatchingPrefixBits(current.Reader.Prefix, current.Reader.PrefixBitCount, firstKey, prefixStart));
        }
        return branchDepth;
    }

    private static int FindOperationsBranchDepth(Span<PbtWriteOperation> operations, int depth, bool validatePrefixes, bool isSorted, TrieUpdaterMetrics? metrics, out bool prefixesValidated)
    {
        prefixesValidated = false;
        if (operations.IsEmpty) return depth;
        PbtFullKey firstKey = operations[0].Key;
        int branchDepth = firstKey.BitLength;
        bool equalLengths = true;
        if (isSorted && !validatePrefixes && operations.Length > 1)
        {
            metrics?.IncrementOperationPrefixComparisons();
            return firstKey.FirstDifferingBit(operations[^1].Key, depth);
        }
        for (int index = 1; index < operations.Length; index++)
        {
            PbtFullKey key = operations[index].Key;
            PbtFullKey reference = isSorted ? operations[index - 1].Key : firstKey;
            metrics?.IncrementOperationPrefixComparisons();
            int difference = reference.FirstDifferingBit(key, depth);
            if (validatePrefixes && difference == Math.Min(reference.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
            branchDepth = Math.Min(branchDepth, difference);
            equalLengths &= key.BitLength == firstKey.BitLength;
        }

        // A single reference does not prove prefix freedom for a variable-length child subset.
        prefixesValidated = validatePrefixes && (isSorted || equalLengths || operations.Length <= 2);
        return branchDepth;
    }

    private static Subtree CreateLeaf(PbtWriteOperation operation) =>
        new(PbtNodeCodec.EncodeLeaf(operation.Key, operation.Value.Bytes), null);

    private static Subtree Resolve(GroupMutationFrame group, Subtree subtree) =>
        subtree.IsEmpty || !subtree.Encoding.IsEmpty ? subtree : group.Take(subtree.Path!, hash: subtree.Hash);

    private static ValueHash256 Place(GroupMutationFrame group, Subtree subtree, int position, int depth)
    {
        if (subtree.IsEmpty) return default;
        // Folding only moves a subtree along its own path, so equal depths imply equal paths.
        if (subtree.Encoding.IsEmpty && depth == subtree.Path!.BitDepth) return subtree.Hash;
        subtree = Resolve(group, subtree);
        PbtNodeReader branch = subtree.Reader;
        if (!branch.IsLeaf && depth != subtree.Path!.BitDepth)
        {
            int pathDepth = subtree.Path.BitDepth;
            int bitCount = pathDepth + branch.PrefixBitCount - depth;
            byte[] encoding = PbtNodeCodec.CreateBranchEncoding(bitCount, branch.LeftHash, branch.RightHash);
            Span<byte> prefix = encoding.AsSpan(3, PbtBitPrefix.ByteCount(bitCount));
            int pathBits = Math.Max(0, pathDepth - depth);
            PbtBitPrefix.CopyBits(subtree.Path.Path, Math.Min(depth, pathDepth), pathBits, prefix, 0);
            int prefixOffset = Math.Max(0, depth - pathDepth);
            PbtBitPrefix.CopyBits(branch.Prefix, prefixOffset, bitCount - pathBits, prefix, pathBits);
            subtree = new(encoding, subtree.Path);
        }
        group.Store(position, subtree);
        return subtree.Hash;
    }

    private static int PrefixBit(Subtree subtree, int bit) => bit < subtree.Path!.BitDepth
        ? (subtree.Path.Path[bit >> 3] >> (7 - (bit & 7))) & 1
        : GetBit(subtree.Reader.Prefix, bit - subtree.Path.BitDepth);

    private static PbtNodePath BoundaryPath(PbtNodePath groupKey, int slot, int level)
    {
        if (level == 0) return groupKey;
        int depth = groupKey.BitDepth + level;
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        path.Clear();
        groupKey.Path.CopyTo(path);
        path[^1] |= (byte)((slot & (0xF << (4 - level))) << (4 - (groupKey.BitDepth & 4)));
        return new PbtNodePath(path, depth);
    }

    /// <summary>A boundary occupant or an unplaced result, retaining the original path of a compressed prefix.</summary>
    private readonly struct Subtree
    {
        internal Subtree(ReadOnlyMemory<byte> encoding, PbtNodePath? path, GroupMutationFrame? owner = null, ValueHash256 hash = default)
        {
            Encoding = encoding;
            Path = path;
            Owner = owner;
            Hash = encoding.IsEmpty ? default : hash != default ? hash : PbtNodeCodec.Hash(new PbtNodeReader(encoding.Span));
        }

        internal Subtree(ValueHash256 hash, PbtNodePath path)
        {
            Hash = hash;
            Path = path;
        }

        internal ReadOnlyMemory<byte> Encoding { get; }
        internal PbtNodeReader Reader => new(Encoding.Span);
        internal GroupMutationFrame? Owner { get; }
        internal PbtNodePath? Path { get; }
        internal ValueHash256 Hash { get; }
        internal bool IsEmpty => Hash == default;

        /// <summary>Copies a result only when it borrows the departing frame's pooled payload.</summary>
        internal Subtree PreserveBeyond(GroupMutationFrame frame) => ReferenceEquals(Owner, frame)
            ? new(Encoding.ToArray(), Path, hash: Hash)
            : this;
    }

    /// <summary>Range knowledge and producer buckets carried through one traversal frame.</summary>
    internal readonly ref struct BucketPlan(ReadOnlySpan<int> precalculated, int depth, int branchDepth, bool isSorted, bool prefixesValidated)
    {
        internal ReadOnlySpan<int> Precalculated { get; } = precalculated;
        internal int Depth { get; } = depth;
        internal int BranchDepth { get; } = branchDepth;
        internal bool IsSorted { get; } = isSorted;
        internal bool PrefixesValidated { get; } = prefixesValidated;

        internal PartitionOutcome BucketSort(Span<PbtWriteOperation> operations, scoped Span<int> offsets, TrieUpdaterMetrics? metrics)
        {
            if (!Precalculated.IsEmpty)
            {
                metrics?.IncrementPrecalculatedLevels();
                int mask = Precalculated[0];
                int countIndex = 1;
                offsets[0] = 0;
                for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
                    offsets[slot + 1] = offsets[slot] + ((mask & (1 << slot)) != 0 ? Precalculated[countIndex++] : 0);
                int bound = BitOperations.IsPow2(mask) ? Depth + PbtFourLevelGroupGeometry.LevelsPerGroup : Depth;
                return new(mask, WithRangeKnowledge(Math.Max(BranchDepth, bound), PrefixesValidated));
            }

            int branchDepth = operations.Length == 1 ? operations[0].Key.BitLength : BranchDepth;
            if (branchDepth < Depth)
                branchDepth = FindOperationsBranchDepth(operations, Depth, false, IsSorted, metrics, out _);
            BucketPlan plan = WithRangeKnowledge(branchDepth, PrefixesValidated);
            if (!operations.IsEmpty && branchDepth >= Depth + PbtFourLevelGroupGeometry.LevelsPerGroup)
            {
                metrics?.IncrementSynthesizedSingleBuckets();
                int slot = BoundarySlot(operations[0].Key, Depth);
                offsets[..(slot + 1)].Clear();
                offsets[(slot + 1)..(PbtFourLevelGroupGeometry.BoundarySlots + 1)].Fill(operations.Length);
                return new(1 << slot, plan);
            }

            if (IsSorted)
                metrics?.IncrementSortedLevels();
            else if (operations.Length <= FullSortThreshold)
            {
                if (operations.Length > 1)
                {
                    metrics?.IncrementFullKeySorts();
                    if (operations.Length <= 3) SortTiny(operations);
                    else operations.Sort(OperationKeyComparer.Instance);
                }
                plan = new(default, Depth, branchDepth, true, PrefixesValidated);
            }
            else
            {
                metrics?.IncrementRadixPartitions();
                return new(BucketizeLarge(operations, Depth, offsets), plan);
            }

            offsets[..(PbtFourLevelGroupGeometry.BoundarySlots + 1)].Clear();
            int usedMask = 0;
            foreach (PbtWriteOperation operation in operations)
            {
                int slot = BoundarySlot(operation.Key, Depth);
                offsets[slot + 1]++;
                usedMask |= 1 << slot;
            }
            for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
                offsets[slot + 1] += offsets[slot];
            return new(usedMask, plan);
        }

        internal BucketPlan WithRangeKnowledge(int branchDepth, bool prefixesValidated) =>
            new(Precalculated, Depth, branchDepth, IsSorted, prefixesValidated);

        internal BucketPlan ForChild(int slot)
        {
            int offset = Precalculated.IsEmpty ? 0 : Precalculated[17 + slot];
            return new(offset == 0 ? default : Precalculated[offset..], Depth + PbtFourLevelGroupGeometry.LevelsPerGroup,
                BranchDepth, IsSorted, PrefixesValidated);
        }

        internal BucketPlan AfterJump(int depth) => new(default, depth, BranchDepth, IsSorted, PrefixesValidated);

        internal BucketPlan AfterFiltering(bool preservesOrder) => new(default, Depth, BranchDepth, IsSorted && preservesOrder, PrefixesValidated);
    }

    /// <summary>The touched buckets and range knowledge established by partitioning.</summary>
    internal readonly ref struct PartitionOutcome(int usedMask, BucketPlan plan)
    {
        internal int UsedMask { get; } = usedMask;
        internal BucketPlan Plan { get; } = plan;
    }

    private static void SortTiny(Span<PbtWriteOperation> operations)
    {
        CompareAndSwap(operations, 0, 1);
        if (operations.Length == 2) return;
        CompareAndSwap(operations, 1, 2);
        CompareAndSwap(operations, 0, 1);
    }

    private static void CompareAndSwap(Span<PbtWriteOperation> operations, int first, int second)
    {
        if (operations[first].Key.CompareTo(operations[second].Key) > 0)
            (operations[first], operations[second]) = (operations[second], operations[first]);
    }

    private sealed class OperationKeyComparer : IComparer<PbtWriteOperation>
    {
        internal static readonly OperationKeyComparer Instance = new();
        public int Compare(PbtWriteOperation left, PbtWriteOperation right) => left.Key.CompareTo(right.Key);
    }

    private static int BucketizeLarge(Span<PbtWriteOperation> operations, int groupDepth, Span<int> offsets)
    {
        Span<int> counts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        counts.Clear();
        int usedMask = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            int bucket = BoundarySlot(operations[index].Key, groupDepth);
            counts[bucket]++;
            usedMask |= 1 << bucket;
        }
        offsets[0] = 0;
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
            offsets[bucket + 1] = offsets[bucket] + counts[bucket];
        if (BitOperations.IsPow2(usedMask)) return usedMask;

        Span<int> next = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        offsets[..PbtFourLevelGroupGeometry.BoundarySlots].CopyTo(next);

        for (int mask = usedMask; mask != 0; mask &= mask - 1)
        {
            int bucket = BitOperations.TrailingZeroCount(mask);
            int end = offsets[bucket + 1];
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
        return usedMask;
    }

    private static int BoundarySlot(PbtFullKey key, int groupDepth) => BoundarySlot(key.Bytes, groupDepth);

    private static int BoundarySlot(ReadOnlySpan<byte> key, int groupDepth)
    {
        byte value = key[groupDepth >> 3];
        return (value >> (4 - (groupDepth & 4))) & 0x0F;
    }

    private static int MatchingPrefixBits(ReadOnlySpan<byte> prefixBytes, int prefixBitCount, PbtFullKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefixBitCount, available);
        ReadOnlySpan<byte> keyBytes = key.Bytes;
        int keyBitOffset = keyOffset & 7;
        int index = 0;
        for (; index + 8 <= count; index += 8)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            int keyByte = keyBitOffset == 0
                ? keyBytes[keyByteIndex]
                : ((keyBytes[keyByteIndex] << 8) | keyBytes[keyByteIndex + 1]) >> (8 - keyBitOffset);
            int difference = prefixBytes[index >> 3] ^ (keyByte & 0xFF);
            if (difference != 0) return index + BitOperations.LeadingZeroCount((uint)difference) - 24;
        }
        while (index < count && GetBit(prefixBytes, index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }

    private static int GetBit(ReadOnlySpan<byte> bytes, int bit) => (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;

    private readonly struct GroupFrameReader : IDisposable
    {
        private readonly RefCountingMemory? _lease;
        private readonly OffsetBuffer _offsets;
        private readonly LengthBuffer _lengths;

        internal GroupFrameReader(IPbtStore store, PbtNodePath groupKey, TrieUpdaterMetrics? metrics)
        {
            GroupKey = groupKey;
            metrics?.IncrementPhysicalGroupFetches();
            _lease = store.GetNodeGroup(groupKey);
            if (_lease is null) return;
            try
            {
                metrics?.IncrementGroupParses();
                PbtNodeGroupReader reader = new(groupKey, _lease.GetSpan());
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    if (!reader.TryGetNodeRange(position, out int offset, out int length)) continue;
                    _offsets[position] = offset;
                    _lengths[position] = length;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal PbtNodePath GroupKey { get; }
        internal int BitDepth => GroupKey.BitDepth;

        internal ReadOnlyMemory<byte> GetEncoding(int position) => _lengths[position] == 0
            ? default
            : _lease!.Memory.Slice(_offsets[position], _lengths[position]);

        internal int Position(PbtNodePath path)
        {
            int completeBytes = BitDepth >> 3;
            int remainingBits = BitDepth & 7;
            if (PbtFourLevelGroupGeometry.GroupDepthOf(path.BitDepth) != BitDepth
                || !path.Path[..completeBytes].SequenceEqual(GroupKey.Path[..completeBytes])
                || (remainingBits != 0 && ((path.Path[completeBytes] ^ GroupKey.Path[completeBytes]) & 0xF0) != 0))
                throw new InvalidOperationException("The PBT node does not belong to the active group.");
            return PbtFourLevelGroupGeometry.PositionOf(path);
        }

        public void Dispose() => ((IDisposable?)_lease)?.Dispose();

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
    }

    private sealed class GroupMutationFrame : IDisposable
    {
        private readonly IPbtStore _store;
        private readonly TrieUpdaterMetrics? _metrics;
        private readonly GroupFrameReader _reader;
        private NodeBuffer _nodes;
        private uint _changed;

        internal GroupMutationFrame(IPbtStore store, PbtNodePath groupKey, TrieUpdaterMetrics? metrics)
        {
            _store = store;
            _metrics = metrics;
            metrics?.IncrementGroupFrameResolutions();
            _reader = new(store, groupKey, metrics);
        }

        internal PbtNodePath GroupKey => _reader.GroupKey;
        internal int BitDepth => _reader.BitDepth;

        internal Subtree Take(PbtNodePath path, bool allowAbsent = false, ValueHash256 hash = default)
        {
            int position = _reader.Position(path);
            Subtree node = _nodes[position];
            if ((_changed & (1U << position)) == 0)
            {
                ReadOnlyMemory<byte> encoding = _reader.GetEncoding(position);
                if (!encoding.IsEmpty) node = new(encoding, path, this, hash);
            }
            if (node.IsEmpty && !allowAbsent) throw new InvalidDataException("A referenced PBT node is missing.");
            _nodes[position] = default;
            _changed |= 1U << position;
            return node.IsEmpty ? default : new(node.Encoding, path, node.Owner, node.Hash);
        }

        internal void Store(int position, Subtree node)
        {
            _nodes[position] = node;
            _changed |= 1U << position;
        }

        internal void Flush()
        {
            if (_changed == 0) return;
            ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtNodeGroupCodec.PositionCount];
            bool[] present = new bool[PbtNodeGroupCodec.PositionCount];
            int changedNodes = 0;
            bool anyPresent = false;
            for (int position = 0; position < encodings.Length; position++)
            {
                ReadOnlyMemory<byte> previous = _reader.GetEncoding(position);
                ReadOnlyMemory<byte> encoding = previous;
                if ((_changed & (1U << position)) != 0)
                {
                    encoding = _nodes[position].Encoding;
                    if (!previous.Span.SequenceEqual(encoding.Span)) changedNodes++;
                }
                encodings[position] = encoding;
                present[position] = !encoding.IsEmpty;
                anyPresent |= present[position];
            }
            if (changedNodes == 0) return;

            if (!anyPresent)
            {
                _store.SetNodeGroup(GroupKey, null);
            }
            else
            {
                BufferWriter writer = new(PooledRefCountingMemoryProvider.Instance);
                try
                {
                    PbtNodeGroupCodec.Encode(ref writer, GroupKey, encodings, present);
                    using RefCountingMemory payload = writer.Detach()!;
                    _store.SetNodeGroup(GroupKey, payload);
                }
                finally
                {
                    writer.Dispose();
                }
            }
            _metrics?.AddEmittedNodeWrites(changedNodes);
        }

        public void Dispose() => _reader.Dispose();

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct NodeBuffer
        {
            private Subtree _element;
        }
    }
}

internal sealed class TrieUpdaterMetrics
{
    internal int PrecalculatedLevels { get; private set; }
    internal int SortedLevels { get; private set; }
    internal int FullKeySorts { get; private set; }
    internal int RadixPartitions { get; private set; }
    internal int OperationPrefixComparisons { get; private set; }
    internal int SynthesizedSingleBuckets { get; private set; }
    internal int PhysicalGroupFetches { get; private set; }
    internal int GroupParses { get; private set; }
    internal int GroupFrameResolutions { get; private set; }
    internal int EmittedNodeWrites { get; private set; }

    internal void Add(TrieUpdaterMetrics metrics)
    {
        PrecalculatedLevels += metrics.PrecalculatedLevels;
        SortedLevels += metrics.SortedLevels;
        FullKeySorts += metrics.FullKeySorts;
        RadixPartitions += metrics.RadixPartitions;
        OperationPrefixComparisons += metrics.OperationPrefixComparisons;
        SynthesizedSingleBuckets += metrics.SynthesizedSingleBuckets;
        PhysicalGroupFetches += metrics.PhysicalGroupFetches;
        GroupParses += metrics.GroupParses;
        GroupFrameResolutions += metrics.GroupFrameResolutions;
        EmittedNodeWrites += metrics.EmittedNodeWrites;
    }

    internal void IncrementPrecalculatedLevels() => PrecalculatedLevels++;
    internal void IncrementSortedLevels() => SortedLevels++;
    internal void IncrementFullKeySorts() => FullKeySorts++;
    internal void IncrementRadixPartitions() => RadixPartitions++;
    internal void IncrementOperationPrefixComparisons() => OperationPrefixComparisons++;
    internal void IncrementSynthesizedSingleBuckets() => SynthesizedSingleBuckets++;
    internal void IncrementPhysicalGroupFetches() => PhysicalGroupFetches++;
    internal void IncrementGroupParses() => GroupParses++;
    internal void IncrementGroupFrameResolutions() => GroupFrameResolutions++;
    internal void AddEmittedNodeWrites(int count) => EmittedNodeWrites += count;
}
