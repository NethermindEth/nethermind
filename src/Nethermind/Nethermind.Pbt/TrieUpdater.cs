// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static partial class TrieUpdater
{
    internal static int GetBit(ReadOnlySpan<byte> bytes, int bit) => (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;

    internal enum NodeKind : byte { Empty, Leaf, Branch }

    /// <summary>Where a boundary node's complete key is read from, which is also whether it is a leaf at all.</summary>
    internal enum LeafSource : byte
    {
        /// <summary>Not a leaf: the encoding is the node's own branch, or there is none.</summary>
        None,
        /// <summary>The left child of the branch the encoding holds.</summary>
        ParentLeft,
        /// <summary>The right child of the branch the encoding holds.</summary>
        ParentRight,
        /// <summary>The encoding is this leaf's own, which only a single-leaf tree's root is stored as.</summary>
        Stored,
    }

    /// <summary>The touched buckets and range knowledge established by partitioning.</summary>
    internal readonly ref struct PartitionOutcome(int usedMask, ReadOnlySpan<int> counts, BucketPlan plan)
    {
        internal int UsedMask { get; } = usedMask;
        /// <summary>Non-empty bucket counts in ascending slot order.</summary>
        internal ReadOnlySpan<int> Counts { get; } = counts;
        internal BucketPlan Plan { get; } = plan;
    }

    /// <summary>Groups consecutive buckets into runs, each holding the operations <paramref name="fanOut"/> asks of the descendants it absorbs.</summary>
    /// <remarks>
    /// A run's minimum follows its own stored descendants rather than the frame's, so a run over buckets with
    /// nothing stored below them stays CPU-bound however large its siblings are. A trailing shortfall joins the
    /// preceding run, so a frame that cannot fill two runs yields one.
    /// </remarks>
    /// <param name="counts">Operation counts per touched bucket, in ascending slot order.</param>
    /// <param name="descendantBytes">The stored size below each of those buckets, in the same order.</param>
    /// <returns>The number of runs; <paramref name="runEnds"/> holds each run's exclusive end bucket index.</returns>
    internal static int PlanBucketRuns(ReadOnlySpan<int> counts, ReadOnlySpan<long> descendantBytes, in FoldFanOut fanOut, Span<int> runEnds)
    {
        Debug.Assert(counts.Length == descendantBytes.Length, "Every touched bucket carries its stored size.");
        int runCount = 0;
        int sum = 0;
        long bytes = 0;
        for (int bucket = 0; bucket < counts.Length; bucket++)
        {
            sum += counts[bucket];
            bytes += descendantBytes[bucket];
            if (sum < fanOut.MinOperationsFor(bytes)) continue;
            runEnds[runCount++] = bucket + 1;
            sum = 0;
            bytes = 0;
        }
        if (runCount == 0) runCount = 1;
        runEnds[runCount - 1] = counts.Length;
        return runCount;
    }

    /// <summary>Charges a starting parallel-loop worker to <paramref name="quota"/>.</summary>
    /// <remarks>
    /// A loop starts only once its caller took one slot, the admission slot, which the first worker thread to start
    /// claims through <paramref name="admissionSlotClaimed"/>; every further worker takes quota outright, so the loop
    /// is charged exactly once per running worker, briefly exceeding the budget when sibling loops start together.
    /// The calling thread already holds its own slot and is not charged.
    /// </remarks>
    /// <returns>Whether the worker holds a slot to hand back through <see cref="ReturnWorkerQuota"/>.</returns>
    internal static bool TakeWorkerQuota(ConcurrencyController quota, int callerThreadId, ref int admissionSlotClaimed)
    {
        if (Environment.CurrentManagedThreadId == callerThreadId) return false;
        if (!ClaimAdmissionSlot(ref admissionSlotClaimed)) quota.TakeConcurrencyQuota();
        return true;
    }

    internal static void ReturnWorkerQuota(ConcurrencyController quota, bool taken)
    {
        if (taken) quota.ReturnConcurrencyQuota();
    }

    /// <summary>Returns the admission slot after the loop unless a worker claimed it and returns it itself.</summary>
    internal static void ReturnAdmissionSlot(ConcurrencyController quota, ref int admissionSlotClaimed)
    {
        if (ClaimAdmissionSlot(ref admissionSlotClaimed)) quota.ReturnConcurrencyQuota();
    }

    private static bool ClaimAdmissionSlot(ref int admissionSlotClaimed) => Interlocked.Exchange(ref admissionSlotClaimed, 1) == 0;

    internal static int BoundarySlot<TKey>(TKey key, int groupDepth) where TKey : struct, IPbtKey<TKey> => BoundarySlot(key.Bytes, groupDepth);

    internal static int BoundarySlot(ReadOnlySpan<byte> key, int groupDepth)
    {
        byte value = key[groupDepth >> 3];
        return (value >> (4 - (groupDepth & 4))) & 0x0F;
    }

}

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Effective mutations are folded through the tree as traversal-local partitioned ranges, so mutations
    /// sharing a path share one traversal. Each completed frame publishes its complete node group.
    /// </remarks>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<TKey> changes) =>
        UpdateRoot(store, currentRoot, changes, null);

    /// <inheritdoc cref="UpdateRoot(IPbtStore, in ValueHash256, PbtWriteBatch{TKey})"/>
    /// <remarks>Groups rewritten by this fold leave prefixless interior branches implicit as <paramref name="prefixlessBranchOmission"/> selects.</remarks>
    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<TKey> changes, PbtPrefixlessBranchOmission prefixlessBranchOmission) =>
        Fold(store, currentRoot, changes, prefixlessBranchOmission, null, null);

    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtWriteBatch<TKey> changes,
        TrieUpdaterMetrics? metrics,
        IRefCountingMemoryProvider? memoryProvider = null) =>
        Fold(store, currentRoot, changes, PbtPrefixlessBranchOmission.Interior, metrics, memoryProvider);

    private static ValueHash256 Fold(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtWriteBatch<TKey> changes,
        PbtPrefixlessBranchOmission prefixlessBranchOmission,
        TrieUpdaterMetrics? metrics,
        IRefCountingMemoryProvider? memoryProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        BucketPlan plan = changes.Plan;
        changes.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<TKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = table;
        return UpdateRoot(store, currentRoot, operations.AsSpan(), changes.ShardNibbleIndex == 0 ? plan : default, prefixlessBranchOmission, metrics, memoryProvider);
    }

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<TKey> changes, TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        changes.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> precalculated);
        using ArrayPoolList<PbtWriteOperation<TKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = precalculated;
        return UpdateRoot(store, currentRoot, operations.AsSpan(), new(precalculated.AsSpan(), 0, false), PbtPrefixlessBranchOmission.Interior, metrics, memoryProvider);
    }

    // Path buffers are cleared by the PbtTraversalPath constructor and the bucket buffer is written by
    // BucketSort before it is read, so the descent frames skip zero-initialization.
    [SkipLocalsInit]
    private static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan, PbtPrefixlessBranchOmission prefixlessBranchOmission, TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider)
    {
        if (operations.IsEmpty) return currentRoot;
        FoldContext context = new(store, memoryProvider ?? PooledRefCountingMemoryProvider.Instance, metrics, null, null, default, prefixlessBranchOmission);
        Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
        PbtTraversalPath path = new(pathBuffer);
        GroupFrameReader<TKey, TPath> reader = new(store, 0, currentRoot, metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
        {
            using PbtNodeGroupWriter<TPath> writer = new(0, context.MemoryProvider, context.PrefixlessBranchOmission);
            BoundaryNode root = reader.TakeRoot(path);
            FoldResult result = FoldMutations(context, ref reader, writer, root, operations, ref path, 0, 0, plan);
            TraversalSubtree resolved = result.Borrow(path);
            ValueHash256 hash = writer.Write(path, PbtFourLevelGroupGeometry.RootPosition, 0, ref resolved, metrics);
            PublishGroup(store, ref reader, writer, path, hash);
            return hash;
        }
    }

    /// <summary>Consumes a subtree and applies its mutation range, returning the canonical replacement.</summary>
    /// <remarks>
    /// Mutations sharing a prefix share traversal through four-bit groups (16 boundary slots).
    /// Shared prefixes skip intermediate groups; boundary folding recursively updates touched slots and recomposes them.
    /// </remarks>
    /// <param name="bitDepth">
    /// Absolute bit offset from the start of the key at which this call partitions the next nibble,
    /// aligned to a four-bit group. The keys' already shared prefix may extend beyond this offset.
    /// This may be deeper than the owner group after skipping a shared prefix.
    /// </param>
    /// <param name="resultDepth">
    /// The group depth of the cursor the caller places the result against, which a prefix jump carries
    /// past the groups it skips. Never deeper than <paramref name="bitDepth"/>.
    /// </param>
    [SkipLocalsInit]
    private static FoldResult FoldMutations(FoldContext context, ref GroupFrameReader<TKey, TPath> ownerReader, PbtNodeGroupWriter<TPath> ownerWriter,
        scoped in BoundaryNode input, Span<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int resultDepth, scoped BucketPlan plan)
    {
        Debug.Assert(path.BitDepth == bitDepth);
        Debug.Assert(resultDepth % PbtFourLevelGroupGeometry.LevelsPerGroup == 0 && resultDepth <= bitDepth);
        TrieUpdaterMetrics? metrics = context.Metrics;
        // Normally the subtree in a boundary slot of the parent group, whose reader/writer are passed here.
        // The initial call supplies the tree root; prefix jumps carry the same subtree to a deeper bitDepth.
        BoundaryNode current = input;
        if (operations.IsEmpty) return current.ToFoldResult(path, resultDepth);

        // At an empty subtree or leaf, a single update needs no partition unless it inserts a different key beside the leaf.
        // A default value denotes deletion, including a no-op when the key is absent.
        if (current.IsEmpty)
        {
            if (operations.Length == 1)
                return operations[0].Value == default ? default : new FoldResult(CreateLeaf(operations[0], metrics));
        }
        else if (current.IsLeaf)
        {
            if (operations.Length == 1)
            {
                PbtWriteOperation<TKey> operation = operations[0];
                if (operation.Key.Equals(current.LeafKey))
                {
                    if (operation.Value == default) return default;
                    // The stored leaf has no value; an unchanged value shows as an unchanged hash.
                    Subtree leaf = CreateLeaf(operation, metrics);
                    return leaf.LeafHash == current.Hash ? current.ToFoldResult(path, resultDepth) : new FoldResult(leaf);
                }
                if (operation.Value == default) return current.ToFoldResult(path, resultDepth);
            }
        }

        // Keys are logically variable-length despite each full-key type using a fixed-size inline buffer;
        // Length/BitLength identify the actual end, not the buffer capacity. Groups advance by a nibble,
        // but complete keys end on byte boundaries. A key ending here has no next nibble to bucket by.
        // Fold it separately from longer keys: deleting 0xAB and inserting 0xABCD must be allowed,
        // while keeping both would violate EIP-8297 prefix freedom.
        if (!TKey.IsFixedLength && bitDepth > 0 && (bitDepth & 7) == 0)
        {
            // Variable-length keys incur an extra linear scan here; fixed-length keys skip this cost.
            int terminalIndex = -1;
            for (int index = 0; index < operations.Length; index++)
            {
                if (operations[index].Key.BitLength != bitDepth) continue;
                terminalIndex = index;
                break;
            }
            bool hasTerminalLeaf = current.IsLeaf && current.LeafKey.BitLength == bitDepth;
            if (terminalIndex >= 0)
            {
                // EIP-8297 prefix freedom applies to surviving keys, after both buckets have been folded.
                BoundaryNode terminal = hasTerminalLeaf ? BoundaryNode.Move(ref current) : default;
                PbtWriteOperation<TKey> operation = operations[terminalIndex];
                operations[..terminalIndex].CopyTo(operations[1..]);
                operations[0] = operation;
                FoldResult terminalResult = FoldMutations(context, ref ownerReader, ownerWriter, terminal, operations[..1], ref path, bitDepth, resultDepth, plan);
                operations = operations[1..];
                plan = plan.AfterFiltering(preservesOrder: true);
                FoldResult descendantResult = FoldMutations(context, ref ownerReader, ownerWriter, current, operations, ref path, bitDepth, resultDepth, plan);
                if (!terminalResult.IsEmpty && !descendantResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                // Either fold may have removed groups below; the surviving result carries both size changes.
                FoldResult result = terminalResult.IsEmpty ? descendantResult : terminalResult;
                result.SizeDelta = terminalResult.SizeDelta + descendantResult.SizeDelta;
                return result;
            }
            if (hasTerminalLeaf)
            {
                BoundaryNode descendants = default;
                FoldResult descendantResult = FoldMutations(context, ref ownerReader, ownerWriter, descendants, operations, ref path, bitDepth, resultDepth, plan);
                if (!descendantResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                FoldResult result = current.ToFoldResult(path, resultDepth);
                result.SizeDelta = descendantResult.SizeDelta;
                return result;
            }
        }

        Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length, bitDepth)];
        PartitionOutcome partition = plan.WithBuffer(buffer).BucketSort(operations, bitDepth, metrics);
        // The existing subtree may diverge before the mutations do. Stop at the four-bit group containing
        // that divergence rather than jumping solely by the mutations' shared prefix.
        TKey firstKey = operations[0].Key;
        int branchDepth = partition.Plan.KnownCommonPrefixLength;
        if (!current.IsEmpty && current.IsLeaf)
        {
            TKey leafKey = current.LeafKey;
            int difference = leafKey.FirstDifferingBit(firstKey, bitDepth);
            branchDepth = Math.Min(branchDepth, difference);
        }
        else if (!current.IsEmpty)
        {
            branchDepth = Math.Min(branchDepth, current.FirstDifferingBit(path, firstKey, bitDepth));
        }
        // Integer floor to the preceding or equal group boundary.
        int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
        // This is the shared-prefix path: both the mutations and existing subtree fit below one slot
        // of this group, so skip ahead. If branching occurs within this group, groupDepth == bitDepth
        // even when branchDepth is a few bits deeper; fold the current group below instead.
        if (groupDepth > bitDepth)
        {
            // This can skip multiple four-bit groups at once, e.g. bitDepth 8 to groupDepth 24.
            // The range's prefix survives the jump; the existing subtree only limits how far we can jump.
            path.AppendKey(firstKey.Bytes, groupDepth);
            FoldResult result = FoldMutations(context, ref ownerReader, ownerWriter, current, operations, ref path, groupDepth, resultDepth, partition.Plan.ForChild());
            path.Truncate(bitDepth);
            // A jump out of the open frame's own partition bypasses SetBoundary, so its slot is charged here.
            if (ownerReader.BitDepth == bitDepth)
            {
                ownerWriter.AddDescendantDelta(BoundarySlot(firstKey.Bytes, bitDepth), result.SizeDelta);
                result.SizeDelta = 0;
            }
            return result;
        }

        // True when the requested group is already open (e.g. the root call at bitDepth 0): reuse its frame.
        // A child call advances bitDepth by four but receives the parent's reader, since its boundary node
        // is stored in that parent group. Then this is false, as it is after a deeper prefix jump;
        // open the descendant group below. The code that opened each frame is responsible for flushing it.
        if (ownerReader.BitDepth == bitDepth)
            return FoldBoundaryFromPartition(context, ref ownerReader, ownerWriter, current, operations, ref path, bitDepth, resultDepth, partition);

        // A deeper group needs its own frame. Publish its completed contents here; the returned subtree root
        // is left for the caller to place, allowing composition to promote it through a compressed path.
        GroupFrameReader<TKey, TPath> reader = new(context.Store, bitDepth, metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
        {
            using PbtNodeGroupWriter<TPath> writer = new(bitDepth, context.MemoryProvider, context.PrefixlessBranchOmission);
            ResolveAbsentGroup(ref reader, in ownerReader, path, current);
            // A group that cannot exist is never keyed: re-anchoring a spanning branch to key it would hash it for nothing.
            if (!reader.IsResolved) reader.SetGroupHash(current.HashAt(path, bitDepth, metrics));
            FoldResult result = FoldBoundaryFromPartition(context, ref reader, writer, current, operations, ref path, bitDepth, resultDepth, partition);
            // The result is anchored where the caller places it, which a jump leaves above this frame.
            TraversalSubtree resolved = result.Borrow(path.Truncated(stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)], resultDepth));
            ValueHash256 hash = resolved.Hash(bitDepth, metrics);
            result.SizeDelta = PublishGroup(context.Store, ref reader, writer, path, hash);
            // The owner group writes this root at the same depth, so a composed root can reuse the hash just published.
            if (result.Node.Kind == NodeKind.Branch) result.Node = result.Node.WithKnownHash(hash, resolved.BranchDepth - bitDepth);
            return result;
        }
    }

    /// <summary>Publishes a frame's group and returns the size change of the group and everything folded below it.</summary>
    /// <remarks>
    /// Each stored descendant size is the loaded (or inherited) size of its slot adjusted by the change folded below that
    /// slot. A group that stays absent stores nothing, so only the folded change survives.
    /// </remarks>
    internal static long PublishGroup(IPbtStore store, ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped in PbtTraversalPath path, in ValueHash256 hash)
    {
        Span<long> descendantBytes = stackalloc long[PbtNodeGroupCodec.DescendantSlots];
        for (int slot = 0; slot < descendantBytes.Length; slot++) descendantBytes[slot] = reader.DescendantBytes(slot) + writer.DescendantDelta(slot);
        using RefCountingMemory? payload = writer.Detach(descendantBytes);
        store.SetNodeGroup(path, hash, payload);
        return (payload?.GetSpan().Length ?? 0) - reader.PayloadLength + writer.DescendantDelta();
    }

    /// <summary>Resolves a frame whose group is provably absent, so the fold never probes the store for it.</summary>
    /// <remarks>
    /// A group holds the nodes strictly below its boundary node. Nothing is stored below an empty subtree or a leaf,
    /// and a branch over two inlined leaves is its own whole subtree, so none of the three owns a group. A branch whose
    /// children lie beyond this group owns no group either, but it still carries the descendant groups the owner counted
    /// under the boundary slot on the way here, so <paramref name="spanningDescendantBytes"/> moves that size here with it.
    /// Any other branch has a child inside the group, which the fold loads on its first access.
    /// </remarks>
    internal static void ResolveAbsentGroup(ref GroupFrameReader<TKey, TPath> reader, scoped in BoundaryNode current, scoped in PbtTraversalPath path, long spanningDescendantBytes)
    {
        // A spanning branch may inline two leaves as well; taking its slot size keeps the owner's accounting.
        if (IsAbsentGroupBelow(current, reader.BitDepth))
            reader.InheritDescendants(BranchSlot(current, path, reader.BitDepth), spanningDescendantBytes);
        else if (OwnsNoGroup(current))
            reader.DeclareAbsent();
    }

    /// <inheritdoc cref="ResolveAbsentGroup(ref GroupFrameReader{TKey, TPath}, in BoundaryNode, in PbtTraversalPath, long)"/>
    /// <remarks>The still-open <paramref name="owner"/> holds the size of everything below the boundary slot on the way here.</remarks>
    internal static void ResolveAbsentGroup(ref GroupFrameReader<TKey, TPath> reader, in GroupFrameReader<TKey, TPath> owner, scoped in PbtTraversalPath path, scoped in BoundaryNode current)
    {
        if (!IsAbsentGroupBelow(current, reader.BitDepth))
        {
            if (OwnsNoGroup(current)) reader.DeclareAbsent();
            return;
        }
        // Only a spanning branch takes a size from its owner, and the fold has read that owner to reach one.
        Debug.Assert(owner.IsResolved, "An owner frame is resolved before the child frame its branch spans into opens.");
        reader.InheritDescendants(BranchSlot(current, path, reader.BitDepth), owner.DescendantBytes(BoundarySlot(path.Bytes, owner.BitDepth)));
    }

    /// <summary>Whether <paramref name="current"/>'s whole subtree is the node itself, which its owner group stores.</summary>
    /// <remarks>LeafChildrenMask decodes the branch encoding, so the empty and leaf kinds are ruled out first.</remarks>
    private static bool OwnsNoGroup(scoped in BoundaryNode current) =>
        current.IsEmpty || current.IsLeaf || current.LeafChildrenMask == (Subtree.LeftLeaf | Subtree.RightLeaf);

    /// <summary>The boundary slot of the group at <paramref name="bitDepth"/> that <paramref name="current"/>'s prefix passes through.</summary>
    internal static int BranchSlot(scoped in BoundaryNode current, scoped in PbtTraversalPath path, int bitDepth)
    {
        Debug.Assert(current.BranchDepth >= bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup);
        int slot = 0;
        for (int bit = bitDepth; bit < bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup; bit++)
            slot = (slot << 1) | current.PrefixBit(path, bit);
        return slot;
    }

    /// <summary>Whether <paramref name="current"/> is a branch whose children lie beyond the group at <paramref name="bitDepth"/>, so that group cannot exist.</summary>
    internal static bool IsAbsentGroupBelow(scoped in BoundaryNode current, int bitDepth)
    {
        Debug.Assert(bitDepth != 0, "The root group always exists.");
        return !current.IsEmpty && !current.IsLeaf && current.BranchDepth >= bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    }

    [SkipLocalsInit]
    private static FoldResult FoldBoundaryFromPartition(
        FoldContext context,
        ref GroupFrameReader<TKey, TPath> reader,
        PbtNodeGroupWriter<TPath> writer,
        BoundaryNode current,
        Span<PbtWriteOperation<TKey>> operations,
        ref PbtTraversalPath path,
        int bitDepth,
        int resultDepth,
        scoped PartitionOutcome partition)
    {
        Debug.Assert(path.BitDepth == bitDepth);
        Frontier frontier = default;
        Decompose(ref reader, path, ref current, bitDepth, ref frontier, partition.UsedMask);

        bool foldedInParallel = context.FoldQuota is not null
            && (partition.UsedMask & (partition.UsedMask - 1)) != 0
            && TryFoldBucketsInParallel(context, ref reader, writer, ref frontier, operations, path, bitDepth, partition);
        if (!foldedInParallel)
            FoldBuckets(context, ref reader, writer, ref frontier, operations, ref path, bitDepth, partition);

        return Compose(ref reader, writer, path, context.Metrics, ref frontier, partition.UsedMask).Materialize(resultDepth);
    }

    private static void FoldBuckets(FoldContext context, scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped ref Frontier frontier, Span<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth,
        scoped PartitionOutcome partition)
    {
        int offset = 0;
        int countIndex = 0;
        for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            int count = partition.Counts[countIndex++];
            Span<PbtWriteOperation<TKey>> bucket = operations.Slice(offset, count);
            offset += count;
            BoundaryNode boundary = TakeBoundary(ref reader, path, ref frontier, slot);
            path.AppendMut(slot);
            FoldResult result = FoldMutations(context, ref reader, writer, boundary,
                bucket, ref path, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, bitDepth, partition.Plan.ForChild());
            path.Truncate(bitDepth);
            TraversalSubtree resolved = result.Borrow(path);
            SetBoundary(ref frontier, slot, ref resolved);
            writer.AddDescendantDelta(slot, result.SizeDelta);
        }
    }

    /// <summary>Folds the runs of touched buckets across threads as quota allows, taking the boundaries before and placing the results after.</summary>
    /// <remarks>
    /// Every child opens and publishes its own group, so the parent frame is only read here and each group
    /// keeps a single writer. Results are materialized copies, so no reader lease crosses threads. Runs fold on
    /// the calling thread while <see cref="FoldContext.FoldQuota"/> has no free slot; the first slot taken admits
    /// a parallel loop over the runs still left, whose workers charge themselves as they start.
    /// </remarks>
    /// <returns>Whether the buckets were folded; false leaves them untouched when they cannot fill two runs.</returns>
    [SkipLocalsInit]
    private static bool TryFoldBucketsInParallel(FoldContext context, scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped ref Frontier frontier, Span<PbtWriteOperation<TKey>> operations, scoped PbtTraversalPath path, int bitDepth,
        scoped PartitionOutcome partition)
    {
        // Read before the boundaries are taken: the planner may refuse to split, which must leave the frontier alone.
        Span<long> descendantBytes = stackalloc long[PbtFourLevelGroupGeometry.BoundarySlots];
        int touched = 0;
        for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
            descendantBytes[touched++] = reader.DescendantBytes(BitOperations.TrailingZeroCount(mask));

        Span<int> runEnds = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        int runCount = PlanBucketRuns(partition.Counts, descendantBytes[..touched], context.FanOut, runEnds);
        if (runCount < 2) return false;

        BucketFold[] buckets = ArrayPool<BucketFold>.Shared.Rent(partition.Counts.Length);
        using ArrayPoolList<int> runs = new(runEnds[..runCount]);
        int offset = OffsetOf(context.Operations!, operations);
        int bucketCount = 0;
        for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            int count = partition.Counts[bucketCount];
            BoundaryNode boundary = TakeBoundary(ref reader, path, ref frontier, slot).Owned();
            buckets[bucketCount] = new BucketFold(slot, offset, count, boundary, descendantBytes[bucketCount], context.Metrics is null ? context : context.WithMetrics(new()));
            bucketCount++;
            offset += count;
        }

        TPath groupPath = path.ToPath<TPath>();
        int knownCommonPrefixLength = partition.Plan.KnownCommonPrefixLength;
        bool isSorted = partition.Plan.IsSorted;
        ConcurrencyController quota = context.FoldQuota!;
        int nextRun = 0;
        for (; nextRun < runs.Count - 1 && !quota.TryRequestConcurrencyQuota(); nextRun++)
            FoldRun(nextRun);
        if (nextRun < runs.Count - 1)
        {
            int callerThreadId = Environment.CurrentManagedThreadId;
            int admissionSlotClaimed = 0;
            try
            {
                ParallelUnbalancedWork.For(nextRun, runs.Count, ParallelUnbalancedWork.DefaultOptions,
                    () => TakeWorkerQuota(quota, callerThreadId, ref admissionSlotClaimed),
                    (index, tookQuota) =>
                    {
                        FoldRun(index);
                        return tookQuota;
                    },
                    tookQuota => ReturnWorkerQuota(quota, tookQuota));
            }
            finally
            {
                ReturnAdmissionSlot(quota, ref admissionSlotClaimed);
            }
        }
        else if (nextRun < runs.Count)
        {
            FoldRun(nextRun);
        }

        foreach (ref BucketFold bucket in buckets.AsSpan(0, bucketCount))
        {
            if (bucket.Context.Metrics is { } bucketMetrics) context.Metrics!.Add(bucketMetrics);
            TraversalSubtree resolved = bucket.Result.Borrow(path);
            SetBoundary(ref frontier, bucket.Slot, ref resolved);
            writer.AddDescendantDelta(bucket.Slot, bucket.Result.SizeDelta);
        }
        // The folds hold node encodings; clear so the pool does not keep them alive.
        ArrayPool<BucketFold>.Shared.Return(buckets, clearArray: true);
        return true;

        void FoldRun(int run)
        {
            for (int bucket = run == 0 ? 0 : runs[run - 1]; bucket < runs[run]; bucket++)
                buckets[bucket].Fold(groupPath, bitDepth, knownCommonPrefixLength, isSorted);
        }
    }

    private static int OffsetOf(PbtWriteOperation<TKey>[] array, Span<PbtWriteOperation<TKey>> span)
    {
        int offset = (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(array), ref MemoryMarshal.GetReference(span)) / Unsafe.SizeOf<PbtWriteOperation<TKey>>());
        Debug.Assert(offset >= 0 && offset + span.Length <= array.Length, "The operation range must lie within the batch array.");
        return offset;
    }

    /// <summary>Per-fold state shared by every frame of one root update.</summary>
    /// <remarks>
    /// <see cref="FoldQuota"/> and <see cref="Operations"/> are set only when wide frames may fold their buckets
    /// concurrently; the former is the budget every nested frame takes its extra workers from before fanning out, the latter
    /// is the backing array of every operation range, so a bucket can rebuild its span on another thread,
    /// and <see cref="FanOut"/> gives how many operations a concurrent run of buckets holds at least, by what it has stored below it.
    /// <see cref="Metrics"/> is not thread-safe, so each concurrent bucket folds under <see cref="WithMetrics"/> and is merged afterwards.
    /// </remarks>
    internal sealed class FoldContext(IPbtStore store, IRefCountingMemoryProvider memoryProvider, TrieUpdaterMetrics? metrics, ConcurrencyController? foldQuota, PbtWriteOperation<TKey>[]? operations, FoldFanOut fanOut, PbtPrefixlessBranchOmission prefixlessBranchOmission)
    {
        internal IPbtStore Store { get; } = store;
        internal IRefCountingMemoryProvider MemoryProvider { get; } = memoryProvider;
        internal TrieUpdaterMetrics? Metrics { get; } = metrics;
        internal ConcurrencyController? FoldQuota { get; } = foldQuota;
        internal PbtWriteOperation<TKey>[]? Operations { get; } = operations;
        internal FoldFanOut FanOut { get; } = fanOut;
        internal PbtPrefixlessBranchOmission PrefixlessBranchOmission { get; } = prefixlessBranchOmission;

        internal FoldContext WithMetrics(TrieUpdaterMetrics metrics) => new(Store, MemoryProvider, metrics, FoldQuota, Operations, FanOut, PrefixlessBranchOmission);
    }

    private struct BucketFold(int slot, int offset, int count, BoundaryNode current, long descendantBytes, FoldContext context)
    {
        internal readonly int Slot = slot;
        internal readonly FoldContext Context = context;
        internal FoldResult Result;

        [SkipLocalsInit]
        internal void Fold(TPath groupPath, int bitDepth, int knownCommonPrefixLength, bool isSorted)
        {
            Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
            PbtTraversalPath path = PbtTraversalPath.FromPath(pathBuffer, groupPath);
            path.AppendMut(Slot);
            // A child below the boundary only reads the owner frame's depth and its slot's descendant size, so a stand-in
            // carrying that size replaces the parent's frame, which must not be shared across threads.
            GroupFrameReader<TKey, TPath> owner = new(Context.Store, bitDepth, null);
            owner.InheritDescendants(Slot, descendantBytes);
            using PbtNodeGroupWriter<TPath> ownerWriter = new(bitDepth, Context.MemoryProvider, Context.PrefixlessBranchOmission);
            Result = FoldMutations(Context, ref owner, ownerWriter, current, Context.Operations!.AsSpan(offset, count),
                ref path, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, bitDepth, new BucketPlan(default, knownCommonPrefixLength, isSorted));
        }
    }

    internal static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    /// <summary>Creates the leaf an insert produces, hashing it once for its parent.</summary>
    private static Subtree CreateLeaf(in PbtWriteOperation<TKey> operation, TrieUpdaterMetrics? metrics)
    {
        metrics?.IncrementNodeHashes();
        TKey key = operation.Key;
        ValueHash256 value = operation.Value;
        return new(key, PbtNodeCodec.HashLeaf(key.Bytes, value.Bytes));
    }

    /// <summary>Takes the boundary node a fold is about to descend into, leaving its slot free for the result.</summary>
    internal static BoundaryNode TakeBoundary(scoped ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path,
        scoped ref Frontier frontier, int slot)
    {
        uint bit = 1u << BoundaryPosition(slot);
        if ((frontier.Mask & bit) == 0) return default;
        frontier.Mask &= ~bit;
        return frontier.TakeBoundaryNode(ref reader, path, slot);
    }

    /// <summary>Takes the node composition stores at <paramref name="position"/>, or empty when neither <paramref name="copies"/> nor the frontier holds one.</summary>
    /// <remarks>
    /// A position in <paramref name="copies"/> is a stored node with no touched slot under it, copied straight from the
    /// frame. Otherwise the frontier hands out a boundary node, a fold's result or a direct copy; this is where each
    /// becomes a node placed against the cursor. A boundary node is the only one anchored above the cursor, so it is the
    /// only one that owns the compressed prefix below the cursor that places it. A stored node is copied, and a position
    /// the group leaves implicit is rebuilt from the children it does store.
    /// </remarks>
    internal static TraversalSubtree TakeSubtree(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        scoped ref Frontier frontier, uint copies, int position)
    {
        uint bit = 1u << position;
        if (((frontier.Mask | copies) & bit) == 0) return default;
        Debug.Assert(position > writer.LastPosition, "Cannot take a PBT node after its output position has passed.");
        if ((copies & bit) != 0) return new TraversalSubtree(path, reader.TakeDirectCopy(path, position));
        TraversalSubtree taken = frontier.Take(ref reader, path, PbtFourLevelGroupGeometry.LocalPathOf(position).Slot, out BoundaryNode boundary);
        frontier.Mask &= ~bit;
        if (!boundary.IsEmpty) return new TraversalSubtree(path, boundary.ToFoldResult(path, path.BitDepth).Node);
        return taken.IsEmpty ? ImplicitBranch(ref reader, path, position) : taken;
    }

    /// <summary>The branch at <paramref name="position"/> that the group leaves implicit, rebuilt from the children it stores.</summary>
    /// <remarks>
    /// Only an interior prefixless branch is ever left out (<see cref="PbtNodeGroupCodec.ShouldOmit"/>), so a
    /// boundary position or the root with nothing stored, or a child missing, is a corrupt group. The link hash
    /// decomposition seeded is kept, so the branch is only hashed again if a sibling's deletion promotes it.
    /// </remarks>
    private static TraversalSubtree ImplicitBranch(scoped ref GroupFrameReader<TKey, TPath> reader, PbtTraversalPath path, int position)
    {
        int width = PbtFourLevelGroupGeometry.WidthOf(position);
        if (width is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
        {
            reader.GetChildHashes(path, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
            if (left != default && right != default)
                return new TraversalSubtree(path, new Subtree(PbtFourLevelGroupGeometry.LocalPathOf(position), left, right, reader.SeededHash(position)));
        }
        throw new InvalidDataException("A referenced PBT node is missing.");
    }

    internal static void SetBoundary(ref Frontier frontier, int slot, ref TraversalSubtree result)
    {
        uint bit = 1u << BoundaryPosition(slot);
        frontier.Mask = result.IsEmpty ? frontier.Mask & ~bit : frontier.Mask | bit;
        frontier.Set(slot, ref result);
    }

    [SkipLocalsInit]
    internal static TraversalSubtree Compose(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, TrieUpdaterMetrics? metrics,
        scoped ref Frontier frontier, int touchedMask)
    {
        // A frame decomposition never read holds a leaf or a spanning branch, so its group stores nothing to copy.
        uint copies = reader.IsResolved ? DirectCopyPositions(reader.StoredPositions(path), touchedMask) : 0;
        ComposeFrameBuffer frames = default;
        // A left child's preimage waits here, one slice per frame, until its sibling is written and both can be hashed at once.
        Span<byte> pendingPreimages = stackalloc byte[(PbtFourLevelGroupGeometry.LevelsPerGroup + 1) * PbtNodeCodec.MaxBranchPreimageLength];
        int frameCount = 1;
        TraversalSubtree result = default;
        while (frameCount != 0)
        {
            ref ComposeFrame frame = ref frames[frameCount - 1];
            Span<byte> leftPreimage = pendingPreimages.Slice((frameCount - 1) * PbtNodeCodec.MaxBranchPreimageLength, PbtNodeCodec.MaxBranchPreimageLength);
            int position = frame.Path.Position;
            int width = frame.Path.Width;
            if (frame.Stage == ComposeStage.LeftCompleted)
            {
                // Establish right occupancy without decoding it, so the left root can be emitted first.
                uint rightMask = ((1u << (width - 1)) - 1) << (position - width + 1);
                if (((frontier.Mask | copies) & rightMask) == 0)
                {
                    frameCount--;
                    continue;
                }

                bool promoteRight = result.IsEmpty;
                if (!promoteRight)
                {
                    // A leaf child is not written; the branch composed below inlines its key.
                    frame.LeftIsLeaf = result.IsLeaf;
                    if (result.IsLeaf) frame.LeftKey = result.Node.LeafKey;
                    frame.LeftHash = writer.Write(path, position - width, reader.BitDepth + frame.Path.Length + 1, ref result, metrics, leftPreimage, out frame.LeftPreimageLength);
                    frame.Stage = ComposeStage.RightCompleted;
                }
                if (width == 2)
                {
                    result = TakeSubtree(ref reader, writer, path, ref frontier, copies, BoundaryPosition(frame.Path.Slot + 1));
                    if (promoteRight) frameCount--;
                }
                else if (promoteRight)
                    frame = new(frame.Path.Right);
                else
                    frames[frameCount++] = new(frame.Path.Right);
                continue;
            }
            if (frame.Stage == ComposeStage.RightCompleted)
            {
                bool rightIsLeaf = result.IsLeaf;
                TKey rightKey = rightIsLeaf ? result.Node.LeafKey : default;
                Span<byte> rightPreimage = pendingPreimages[^PbtNodeCodec.MaxBranchPreimageLength..];
                ValueHash256 rightHash = writer.Write(path, position - 1, reader.BitDepth + frame.Path.Length + 1, ref result, metrics, rightPreimage, out int rightPreimageLength);
                HashPending(leftPreimage[..frame.LeftPreimageLength], ref frame.LeftHash, rightPreimage[..rightPreimageLength], ref rightHash, metrics);
                byte leafChildren = (byte)((frame.LeftIsLeaf ? Subtree.LeftLeaf : 0) | (rightIsLeaf ? Subtree.RightLeaf : 0));
                result = new TraversalSubtree(path, new Subtree(frame.Path, frame.LeftHash, rightHash, frame.LeftKey, rightKey, leafChildren));
                frameCount--;
                continue;
            }
            result = TakeSubtree(ref reader, writer, path, ref frontier, copies, position);
            if (!result.IsEmpty)
            {
                // An internal frontier entry is an unchanged subtree reached from the original input.
                // Copy descendants only: its root may still be promoted by an updated sibling's deletion.
                if (!result.IsLeaf)
                {
                    int copied = reader.CopyRange(path, writer, position - 2 * width + 2, position);
                    if (copied != 0) metrics?.AddBulkCopy(copied);
                }
                frameCount--;
                continue;
            }

            frame.Stage = ComposeStage.LeftCompleted;
            if (width == 2)
                result = TakeSubtree(ref reader, writer, path, ref frontier, copies, BoundaryPosition(frame.Path.Slot));
            else
                frames[frameCount++] = new(frame.Path.Left);
        }
        return result;
    }

    /// <summary>Hashes the sibling preimages left pending by <see cref="PbtNodeGroupWriter{TPath}.Write{TKey}(in PbtTraversalPath, int, int, ref TraversalSubtree, TrieUpdaterMetrics?, scoped Span{byte}, out int)"/>, together when both are pending.</summary>
    private static void HashPending(ReadOnlySpan<byte> leftPreimage, ref ValueHash256 leftHash, ReadOnlySpan<byte> rightPreimage, ref ValueHash256 rightHash, TrieUpdaterMetrics? metrics)
    {
        if (leftPreimage.IsEmpty && rightPreimage.IsEmpty) return;
        if (leftPreimage.IsEmpty)
        {
            metrics?.IncrementNodeHashes();
            rightHash = Blake3Hash.Hash(rightPreimage);
        }
        else if (rightPreimage.IsEmpty)
        {
            metrics?.IncrementNodeHashes();
            leftHash = Blake3Hash.Hash(leftPreimage);
        }
        else
        {
            metrics?.AddNodeHashes(2);
            Blake3Hash.HashTwo(leftPreimage, rightPreimage, out leftHash, out rightHash);
        }
    }

    private enum ComposeStage : byte { Descend, LeftCompleted, RightCompleted }

    private struct ComposeFrame(NodeGroupPath path)
    {
        internal NodeGroupPath Path = path;
        internal ComposeStage Stage;
        internal ValueHash256 LeftHash;
        /// <summary>The length of the left child's preimage awaiting its sibling, or zero when its hash is already known.</summary>
        internal int LeftPreimageLength;
        internal TKey LeftKey;
        internal bool LeftIsLeaf;
    }

    [InlineArray(PbtFourLevelGroupGeometry.LevelsPerGroup)]
    private struct ComposeFrameBuffer
    {
        private ComposeFrame _element;
    }

    /// <summary>Resolves the input into touched boundary nodes and opaque untouched siblings.</summary>
    /// <remarks>
    /// Every entry is resolved from its own position upwards, against the deepest node the group stores above that
    /// position, or against the input when it stores none. That node's link is what both names the entry and holds its
    /// hash, so entries are placed without hashing anything. Entries use their leftmost boundary slot; the mask retains
    /// their group positions. An opaque subtree and its descendants never coexist, so their slots cannot collide. A
    /// stored node with no touched slot under it gets no entry: <see cref="Compose"/> copies it from the stored and
    /// touched positions alone, so only its link hash is seeded here.
    /// </remarks>
    [SkipLocalsInit]
    internal static void Decompose(ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path, ref BoundaryNode input,
        int bitDepth, ref Frontier frontier, int touchedMask)
    {
        if (input.IsEmpty) return;
        frontier.Root = BoundaryNode.Move(ref input);
        int boundaryDepth = bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        // A leaf, or a branch reaching past this group, is the whole subtree under one slot and needs no group read.
        if (frontier.Root.IsLeaf)
        {
            // The key is decoded once: a span taken straight off the property would point at an unnamed temporary.
            TKey rootLeafKey = frontier.Root.LeafKey;
            int leafSlot = BoundarySlot(rootLeafKey.Bytes, bitDepth);
            frontier.Place(leafSlot, BoundaryPosition(leafSlot), EntrySource.AtPosition, RootSource);
            return;
        }
        if (frontier.Root.BranchDepth >= boundaryDepth)
        {
            int spanningSlot = BranchSlot(frontier.Root, path, bitDepth);
            frontier.Place(spanningSlot, BoundaryPosition(spanningSlot), EntrySource.AtPosition, RootSource);
            return;
        }

        // The root is held by the frontier, not by the group, so its position never answers the climb.
        uint stored = reader.StoredPositions(path) & ~(1u << PbtFourLevelGroupGeometry.RootPosition);
        uint copies = DirectCopyPositions(stored, touchedMask);
        for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots;)
        {
            // A touched slot is resolved on its own; everything else in the widest aligned untouched block it starts.
            int width = 1;
            if ((touchedMask >> slot & 1) == 0)
                while (width < PbtFourLevelGroupGeometry.BoundarySlots
                       && (slot & (2 * width - 1)) == 0
                       && (touchedMask & (((1 << (2 * width)) - 1) << slot)) == 0)
                    width <<= 1;
            ResolveBlock(ref reader, path, bitDepth, stored, copies, slot, width, ref frontier);
            slot += width;
        }
    }

    /// <summary>Places the entry for the aligned block of <paramref name="width"/> slots starting at <paramref name="slot"/>.</summary>
    /// <remarks>
    /// What covers the block is the deepest node stored above its position, because a node carrying a compressed prefix
    /// is always stored at its anchor and only prefixless interior branches are left implicit. That node either spans
    /// the block itself, holds the block's whole subtree as an inlined leaf, or names it through a link, whose hash is
    /// seeded at the level the link addresses rather than at the block's own.
    /// </remarks>
    private static void ResolveBlock(ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path,
        int bitDepth, uint stored, uint copies, int slot, int width, ref Frontier frontier)
    {
        int level = PbtFourLevelGroupGeometry.LevelsPerGroup - BitOperations.Log2((uint)width);
        int position = new NodeGroupPath(slot, level).Position;
        int depth = bitDepth + level;
        int covering = DeepestStoredAbove(stored, position);
        SpineNode node = NodeAt(ref reader, path, frontier.Root, bitDepth, covering);

        int branchDepth = node.BranchDepth;
        for (int bit = Math.Max(node.AnchorDepth, bitDepth); bit < Math.Min(branchDepth, depth); bit++)
            if (GetBit(node.Prefix.Bytes, bit - node.AnchorDepth) != SlotBit(slot, bitDepth, bit)) return;

        if (branchDepth >= depth)
        {
            // The covering node is the only subtree under this block, so it settles where its own branch reaches.
            int entryLevel = Math.Min(branchDepth, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup) - bitDepth;
            int entrySlot = 0;
            // Above its anchor the covering node's path is this block's own, since it is stored on the block's chain.
            for (int bit = bitDepth; bit < bitDepth + entryLevel; bit++)
                entrySlot = (entrySlot << 1) | (bit < node.AnchorDepth ? SlotBit(slot, bitDepth, bit) : GetBit(node.Prefix.Bytes, bit - node.AnchorDepth));
            entrySlot <<= PbtFourLevelGroupGeometry.LevelsPerGroup - entryLevel;
            if (covering >= 0) SeedLinkHash(ref reader, path, bitDepth, stored, covering, ref frontier);
            frontier.Place(entrySlot, new NodeGroupPath(entrySlot, entryLevel).Position, EntrySource.AtPosition,
                covering < 0 ? RootSource : covering);
            return;
        }

        int side = SlotBit(slot, bitDepth, branchDepth);
        PbtNodeReader branch = node.Node;
        ReadOnlySpan<byte> leafKey = side == 0 ? branch.LeftKey : branch.RightKey;
        if (!leafKey.IsEmpty)
        {
            // An inlined leaf is the whole subtree under its link, which is wider than this block when the link is
            // shallower than it; the block that holds the leaf's own slot claims it, the others hold nothing.
            int leafSlot = BoundarySlot(leafKey, bitDepth);
            if ((uint)(leafSlot - slot) >= (uint)width) return;
            frontier.Place(leafSlot, BoundaryPosition(leafSlot), side == 0 ? EntrySource.LeftLeafOf : EntrySource.RightLeafOf,
                covering < 0 ? RootSource : covering);
            return;
        }

        // The link names a node, so every level between it and this block holds a prefixless branch left implicit,
        // and this block's position holds a node of its own. Only the link's own level carries the hash.
        int linkLevel = branchDepth + 1 - bitDepth;
        int linkPosition = new NodeGroupPath(slot & ~((PbtFourLevelGroupGeometry.BoundarySlots >> linkLevel) - 1), linkLevel).Position;
        reader.SeedHash(linkPosition, side == 0 ? branch.LeftHash : branch.RightHash);
        Debug.Assert(linkPosition == position || level < PbtFourLevelGroupGeometry.LevelsPerGroup,
            "A boundary node is never left implicit, so its link addresses it directly.");
        if ((copies & (1u << position)) == 0) frontier.Place(slot, position, EntrySource.AtPosition, position);
    }

    /// <summary>Seeds the hash of the node stored at <paramref name="position"/> from the link that names it.</summary>
    /// <remarks>
    /// A level left implicit carries no link, so a node below one keeps the single hash its own encoding needs, which
    /// the frame computes once on demand. Nothing is hashed here.
    /// </remarks>
    private static void SeedLinkHash(ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path,
        int bitDepth, uint stored, int position, ref Frontier frontier)
    {
        if (reader.SeededHash(position) != default) return;
        NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
        SpineNode node = NodeAt(ref reader, path, frontier.Root, bitDepth, DeepestStoredAbove(stored, position));
        int linkDepth = bitDepth + local.Length;
        if (node.BranchDepth != linkDepth - 1) return;
        int side = local.GetBit(local.Length - 1);
        reader.SeedHash(position, side == 0 ? node.Node.LeftHash : node.Node.RightHash);
    }

    /// <summary>The stored positions below the group root with no touched slot under them, which composition copies unchanged.</summary>
    private static uint DirectCopyPositions(uint stored, int touchedMask)
    {
        uint copies = 0;
        for (uint remaining = stored & ~(1u << PbtFourLevelGroupGeometry.RootPosition); remaining != 0; remaining &= remaining - 1)
        {
            int position = BitOperations.TrailingZeroCount(remaining);
            NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
            if ((touchedMask & (((1 << local.Width) - 1) << local.Slot)) == 0) copies |= 1u << position;
        }
        return copies;
    }

    /// <summary>The deepest position above <paramref name="position"/> that the group stores a node at, or -1 for its root.</summary>
    private static int DeepestStoredAbove(uint stored, int position)
    {
        int ancestor = PbtFourLevelGroupGeometry.ParentOf(position);
        while (ancestor >= 0 && (stored & (1u << ancestor)) == 0) ancestor = PbtFourLevelGroupGeometry.ParentOf(ancestor);
        return ancestor;
    }

    /// <summary>The bit at <paramref name="bit"/> of the path to a boundary slot, which only spans this group.</summary>
    private static int SlotBit(int slot, int bitDepth, int bit) =>
        (slot >> (bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup - 1 - bit)) & 1;

    /// <summary>A node the decomposition reads for its links: one the group stores, or the group's own root.</summary>
    private readonly ref struct SpineNode(PbtNodeReader node, int anchorDepth)
    {
        internal readonly PbtNodeReader Node = node;
        /// <summary>The absolute depth this node's compressed prefix starts at, above the group for a spanning input.</summary>
        internal readonly int AnchorDepth = anchorDepth;
        internal CompressedPrefix Prefix => Node.Prefix;
        internal int BranchDepth => AnchorDepth + Node.Prefix.BitCount;
    }

    private static SpineNode NodeAt(ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path, scoped in BoundaryNode root, int bitDepth, int position) =>
        position < 0
            ? new SpineNode(root.Reader, root.AnchorDepth)
            : new SpineNode(PbtNodeReader.FromValidated(reader.GetEncoding(path, position).Span),
                bitDepth + PbtFourLevelGroupGeometry.LocalPathOf(position).Length);

    internal static int MatchingPrefixBits(CompressedPrefix prefix, TKey key, int keyOffset)
    {
        if (prefix.BitCount == 0) return 0;

        ReadOnlySpan<byte> prefixBytes = prefix.Bytes;
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        ReadOnlySpan<byte> keyBytes = key.Bytes;
        int keyBitOffset = keyOffset & 7;
        if (keyBitOffset == 0)
            return Math.Min(count, PbtKeyOperations.FirstDifferingBit(prefixBytes, keyBytes[(keyOffset >> 3)..], 0));

        int index = 0;
        for (; index + 64 <= count; index += 64)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            ulong keyWord = (BinaryPrimitives.ReadUInt64BigEndian(keyBytes[keyByteIndex..]) << keyBitOffset)
                | (uint)(keyBytes[keyByteIndex + sizeof(ulong)] >> (8 - keyBitOffset));
            ulong difference = BinaryPrimitives.ReadUInt64BigEndian(prefixBytes[(index >> 3)..]) ^ keyWord;
            if (difference != 0) return index + BitOperations.LeadingZeroCount(difference);
        }
        for (; index + 8 <= count; index += 8)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            int keyByte = ((keyBytes[keyByteIndex] << 8) | keyBytes[keyByteIndex + 1]) >> (8 - keyBitOffset);
            int difference = prefixBytes[index >> 3] ^ (keyByte & 0xFF);
            if (difference != 0) return index + BitOperations.LeadingZeroCount((uint)difference) - 24;
        }
        while (index < count && GetBit(prefixBytes, index) == key.GetBit(keyOffset + index)) index++;
        return index;
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
    internal int BulkCopiedNodes { get; private set; }
    internal int BulkCopyOperations { get; private set; }
    /// <summary>Node hashes computed from an encoding, excluding those reused from a parent node.</summary>
    internal int NodeHashes { get; private set; }

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
        BulkCopiedNodes += metrics.BulkCopiedNodes;
        BulkCopyOperations += metrics.BulkCopyOperations;
        NodeHashes += metrics.NodeHashes;
    }

    internal void AddBulkCopy(int nodes)
    {
        BulkCopiedNodes += nodes;
        BulkCopyOperations++;
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
    internal void IncrementNodeHashes() => NodeHashes++;
    internal void AddNodeHashes(int count) => NodeHashes += count;
}
