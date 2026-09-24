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
    where TKey : unmanaged, IPbtKey<TKey>
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
        using IPbtConcurrentWriter storeWriter = store.CreateWriter();
        FoldContext context = new(store, storeWriter, memoryProvider ?? PooledRefCountingMemoryProvider.Instance, metrics, null, null, default, prefixlessBranchOmission);
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
            PublishGroup(storeWriter, ref reader, writer, path, hash);
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
                return operations[0].Value == default ? default : CreateLeaf(operations[0], metrics);
        }
        else if (current.IsLeaf)
        {
            if (operations.Length == 1)
            {
                PbtWriteOperation<TKey> operation = operations[0];
                TKey leafKey = current.LeafKey;
                if (operation.Key.Equals(leafKey))
                {
                    if (operation.Value == default) return default;
                    // The stored leaf has no value; an unchanged value shows as an unchanged hash.
                    FoldResult leaf = CreateLeaf(operation, metrics);
                    return leaf.LeafHash == current.Hash ? current.ToFoldResult(path, resultDepth) : leaf;
                }
                if (operation.Value == default) return current.ToFoldResult(path, resultDepth);
                // An insert beside a leaf is a branch over the two, which owns no group, so no frame is opened for it.
                int divergenceDepth = leafKey.FirstDifferingBit(operation.Key, bitDepth);
                if (divergenceDepth < Math.Min(leafKey.BitLength, operation.Key.BitLength))
                    return TwoLeafBranch(new FoldResult(leafKey, current.Hash), CreateLeaf(operation, metrics), divergenceDepth, resultDepth);
            }
        }
        else if (operations.Length == 1 && current.LeafChildrenMask == (Subtree.LeftLeaf | Subtree.RightLeaf))
        {
            // A branch over two inlined leaves is its whole subtree, so a write to either leaf rewrites it in place.
            PbtWriteOperation<TKey> operation = operations[0];
            bool right = operation.Key.Equals(current.RightLeafKey);
            if (right || operation.Key.Equals(current.LeftLeafKey))
            {
                if (operation.Value == default)
                    return right ? new FoldResult(current.LeftLeafKey, current.LeftHash) : new FoldResult(current.RightLeafKey, current.RightHash);
                FoldResult leaf = CreateLeaf(operation, metrics);
                if (leaf.LeafHash == (right ? current.RightHash : current.LeftHash)) return current.ToFoldResult(path, resultDepth);
                FoldResult branch = current.ToFoldResult(path, resultDepth);
                return new FoldResult(branch.Path, right ? branch.LeftHash : leaf.LeafHash, right ? leaf.LeafHash : branch.RightHash,
                    branch.LeafKey, branch.RightLeafKey, branch.LeafChildren, branch.Encoding);
            }
            if (operation.Value == default) return current.ToFoldResult(path, resultDepth);
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

        return FoldInOwnFrame(context, in ownerReader, current, operations, ref path, bitDepth, resultDepth, partition);
    }

    /// <summary>Folds a group deeper than the open frame in a frame of its own, publishing it before returning its root.</summary>
    /// <remarks>
    /// The returned subtree root is left for the caller to place, allowing composition to promote it through a
    /// compressed path. Kept out of line: the frame and the node temporaries hold references, so the JIT zeroes their
    /// stack space on entry, which inlined would be paid by every fold that reuses its owner's frame.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static FoldResult FoldInOwnFrame(FoldContext context, in GroupFrameReader<TKey, TPath> ownerReader, scoped in BoundaryNode current,
        Span<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int resultDepth, scoped PartitionOutcome partition)
    {
        TrieUpdaterMetrics? metrics = context.Metrics;
        GroupFrameReader<TKey, TPath> reader = new(context.Store, bitDepth, metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
        {
            using PbtNodeGroupWriter<TPath> writer = new(bitDepth, context.MemoryProvider, context.PrefixlessBranchOmission);
            ResolveAbsentGroup(ref reader, in ownerReader, path, current);
            // A group that cannot exist is never keyed: re-anchoring a spanning branch to key it would hash it for nothing.
            if (!reader.IsResolved) reader.SetGroupHash(current.HashAt(path, bitDepth, metrics));
            FoldResult result = FoldBoundaryFromPartition(context, ref reader, writer, current, operations, ref path, bitDepth, resultDepth, partition);
            // The result is anchored where the caller places it, which a jump leaves above this frame.
            PbtTraversalPath resultCursor = path.Truncated(stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)], resultDepth);
            ValueHash256 hash = result.Hash(resultCursor, bitDepth, metrics);
            result.SizeDelta = PublishGroup(context.Writer, ref reader, writer, path, hash);
            // The owner group writes this root at the same depth, so a composed root can reuse the hash just published.
            if (result.Kind == NodeKind.Branch) result = result.WithKnownHash(hash, result.BranchDepth(resultCursor) - bitDepth);
            return result;
        }
    }

    /// <summary>Publishes a frame's group and returns the size change of the group and everything folded below it.</summary>
    /// <remarks>
    /// Each stored descendant size is the loaded (or inherited) size of its slot adjusted by the change folded below that
    /// slot. A group that stays absent stores nothing, so only the folded change survives, and the store is not told
    /// to delete a group it never held.
    /// </remarks>
    internal static long PublishGroup(IPbtNodeGroupSink sink, ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped in PbtTraversalPath path, in ValueHash256 hash)
    {
        Span<long> descendantBytes = stackalloc long[PbtNodeGroupCodec.DescendantSlots];
        for (int slot = 0; slot < descendantBytes.Length; slot++) descendantBytes[slot] = reader.DescendantBytes(slot) + writer.DescendantDelta(slot);
        using RefCountingMemory? payload = writer.Detach(descendantBytes);
        Debug.Assert(reader.IsResolved, "A frame is loaded or declared absent before it publishes, so an empty payload length means no stored group.");
        if (payload is not null || reader.PayloadLength != 0) sink.SetNodeGroup(path, hash, payload);
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
        Frontier frontier = new(partition.UsedMask);
        Span<FoldResult> results = stackalloc FoldResult[BitOperations.PopCount((uint)partition.UsedMask)];
        Decompose(ref reader, path, ref current, bitDepth, ref frontier, partition.UsedMask);

        bool foldedInParallel = context.FoldQuota is not null
            && (partition.UsedMask & (partition.UsedMask - 1)) != 0
            && TryFoldBucketsInParallel(context, ref reader, writer, ref frontier, results, operations, path, bitDepth, partition);
        if (!foldedInParallel)
            FoldBuckets(context, ref reader, writer, ref frontier, results, operations, ref path, bitDepth, partition);

        return Compose(ref reader, writer, path, resultDepth, context.Metrics, ref frontier, results);
    }

    private static void FoldBuckets(FoldContext context, scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped ref Frontier frontier, scoped Span<FoldResult> results, Span<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth,
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
            writer.AddDescendantDelta(slot, result.SizeDelta);
            SetBoundary(ref frontier, results, slot, ref result);
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
        scoped ref Frontier frontier, scoped Span<FoldResult> results, Span<PbtWriteOperation<TKey>> operations, scoped PbtTraversalPath path, int bitDepth,
        scoped PartitionOutcome partition)
    {
        // Two runs each hold at least the smaller minimum, so a frame with fewer operations never splits.
        FoldFanOut fanOut = context.FanOut;
        if (operations.Length < 2 * Math.Min(fanOut.MinOperationsPerWorker, fanOut.LargeSubtreeMinOperationsPerWorker)) return false;

        // Read before the boundaries are taken: the planner may refuse to split, which must leave the frontier alone.
        Span<long> descendantBytes = stackalloc long[PbtFourLevelGroupGeometry.BoundarySlots];
        int touched = 0;
        for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
            descendantBytes[touched++] = reader.DescendantBytes(BitOperations.TrailingZeroCount(mask));

        Span<int> runEnds = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        int runCount = PlanBucketRuns(partition.Counts, descendantBytes[..touched], fanOut, runEnds);
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
            writer.AddDescendantDelta(bucket.Slot, bucket.Result.SizeDelta);
            SetBoundary(ref frontier, results, bucket.Slot, ref bucket.Result);
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
    /// Groups are read from <see cref="Store"/> but published to <see cref="Writer"/>, which a concurrent bucket replaces
    /// with its own <see cref="IPbtStore.CreateWriter"/> so it never writes the store directly.
    /// </remarks>
    internal sealed class FoldContext(IPbtStore store, IPbtNodeGroupSink writer, IRefCountingMemoryProvider memoryProvider, TrieUpdaterMetrics? metrics, ConcurrencyController? foldQuota, PbtWriteOperation<TKey>[]? operations, FoldFanOut fanOut, PbtPrefixlessBranchOmission prefixlessBranchOmission)
    {
        internal IPbtStore Store { get; } = store;
        internal IPbtNodeGroupSink Writer { get; } = writer;
        internal IRefCountingMemoryProvider MemoryProvider { get; } = memoryProvider;
        internal TrieUpdaterMetrics? Metrics { get; } = metrics;
        internal ConcurrencyController? FoldQuota { get; } = foldQuota;
        internal PbtWriteOperation<TKey>[]? Operations { get; } = operations;
        internal FoldFanOut FanOut { get; } = fanOut;
        internal PbtPrefixlessBranchOmission PrefixlessBranchOmission { get; } = prefixlessBranchOmission;

        internal FoldContext WithMetrics(TrieUpdaterMetrics metrics) => new(Store, Writer, MemoryProvider, metrics, FoldQuota, Operations, FanOut, PrefixlessBranchOmission);

        internal FoldContext WithWriter(IPbtNodeGroupSink writer) => new(Store, writer, MemoryProvider, Metrics, FoldQuota, Operations, FanOut, PrefixlessBranchOmission);
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
            using IPbtConcurrentWriter writer = Context.Store.CreateWriter();
            Result = FoldMutations(Context.WithWriter(writer), ref owner, ownerWriter, current, Context.Operations!.AsSpan(offset, count),
                ref path, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, bitDepth, new BucketPlan(default, knownCommonPrefixLength, isSorted));
        }
    }

    internal static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    /// <summary>Creates the leaf an insert produces, hashing it once for its parent.</summary>
    private static FoldResult CreateLeaf(in PbtWriteOperation<TKey> operation, TrieUpdaterMetrics? metrics)
    {
        metrics?.IncrementNodeHashes();
        TKey key = operation.Key;
        ValueHash256 value = operation.Value;
        return new(key, PbtNodeCodec.HashLeaf(key.Bytes, value.Bytes));
    }

    /// <summary>The branch over two leaves whose keys first differ at <paramref name="branchDepth"/>, read against the cursor at <paramref name="anchorDepth"/>.</summary>
    private static FoldResult TwoLeafBranch(in FoldResult first, in FoldResult second, int branchDepth, int anchorDepth)
    {
        TKey key = first.LeafKey;
        bool firstIsLeft = key.GetBit(branchDepth) == 0;
        ref readonly FoldResult left = ref firstIsLeft ? ref first : ref second;
        ref readonly FoldResult right = ref firstIsLeft ? ref second : ref first;
        int localLength = Math.Min(branchDepth - anchorDepth, PbtFourLevelGroupGeometry.LevelsPerGroup);
        int slot = 0;
        for (int bit = anchorDepth; bit < anchorDepth + localLength; bit++) slot = (slot << 1) | key.GetBit(bit);
        int prefixBitCount = branchDepth - anchorDepth - localLength;
        scoped Span<byte> prefix = default;
        if (prefixBitCount != 0)
        {
            prefix = stackalloc byte[sizeof(ushort) + PbtBitPrefix.ByteCount(prefixBitCount)];
            // Zeroed, because the bits are copied in by disjunction.
            prefix.Clear();
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)prefixBitCount);
            PbtBitPrefix.CopyBits(key.Bytes, anchorDepth + localLength, prefixBitCount, prefix[sizeof(ushort)..], 0);
        }
        return new FoldResult(new NodeGroupPath(slot << (PbtFourLevelGroupGeometry.LevelsPerGroup - localLength), localLength),
            left.LeafHash, right.LeafHash, left.LeafKey, right.LeafKey, Subtree.LeftLeaf | Subtree.RightLeaf, prefix);
    }

    /// <summary>Takes the boundary node a fold is about to descend into, which <see cref="SetBoundary"/> then replaces with the fold's result.</summary>
    internal static BoundaryNode TakeBoundary(scoped ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path,
        scoped ref Frontier frontier, int slot)
    {
        uint bit = 1u << BoundaryPosition(slot);
        if ((frontier.Mask & bit) == 0) return default;
        return frontier.TakeBoundaryNode(ref reader, path, slot);
    }

    /// <summary>Appends the node the frontier holds at <paramref name="position"/>, after the descendants the group keeps under it.</summary>
    /// <remarks>
    /// The frontier hands out a fold's result, a boundary node or an untouched block. A fold's result and a boundary node
    /// are anchored at the position that holds them, so each is written with the compressed prefix it owns. An untouched
    /// block is read from the deepest node the group stores above it, which is written with its compressed prefix past
    /// this position only, and so hashed again. A position the group leaves implicit is rebuilt from the children it
    /// does store. Only the descendants are copied: the node itself may still be promoted by an updated sibling's deletion.
    /// </remarks>
    internal static ComposedNode AppendHeld(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        scoped ref Frontier frontier, scoped Span<FoldResult> results, int position, TrieUpdaterMetrics? metrics)
    {
        Debug.Assert((frontier.Mask & (1u << position)) != 0, "Only a held position is taken.");
        Debug.Assert(position > writer.LastPosition, "Cannot take a PBT node after its output position has passed.");
        NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
        int copied = reader.CopyRange(path, writer, position - 2 * local.Width + 2, position);
        if (copied != 0) metrics?.AddBulkCopy(copied);
        ref readonly DecompositionEntry entry = ref frontier.Entries[local.Slot];
        switch (entry.Source)
        {
            case EntrySource.Node:
                return AppendResult(writer, position, frontier.TakeResult(results, local.Slot));
            case EntrySource.AtPosition when entry.SourcePosition != RootSource:
                ReadOnlyMemory<byte> stored = reader.GetEncoding(path, entry.SourcePosition);
                return stored.IsEmpty
                    ? AppendImplicitBranch(ref reader, writer, path, position)
                    : AppendReanchored(writer, position, local.Length - PbtFourLevelGroupGeometry.LocalPathOf(entry.SourcePosition).Length,
                        PbtNodeReader.FromValidated(stored.Span));
            default:
                return AppendResult(writer, position, frontier.TakeBoundaryNode(ref reader, path, local.Slot).ToFoldResult(path, path.BitDepth));
        }
    }

    /// <summary>Appends a fold's result anchored at <paramref name="position"/>, with the compressed prefix it owns.</summary>
    private static ComposedNode AppendResult(PbtNodeGroupWriter<TPath> writer, int position, in FoldResult node)
    {
        int offset = writer.WrittenCount;
        TKey leftKey = node.LeafKey;
        if (node.IsLeaf)
        {
            int leafLength = PbtNodeCodec.LeafLength(leftKey.Length);
            PbtNodeCodec.EncodeLeaf(writer.Append(position, leafLength), leftKey);
            return new(offset, leafLength, node.LeafHash);
        }

        Debug.Assert(node.Path.Length == PbtFourLevelGroupGeometry.LocalPathOf(position).Length, "A held result is anchored at the position that holds it.");
        CompressedPrefix prefix = node.Encoding.IsEmpty ? default : CompressedPrefix.FromValidated(node.Encoding);
        TKey rightKey = node.RightLeafKey;
        int leftKeyLength = node.HasLeftLeaf ? leftKey.Length : 0;
        int rightKeyLength = node.HasRightLeaf ? rightKey.Length : 0;
        int length = PbtNodeCodec.BranchLength(prefix.BitCount, leftKeyLength, rightKeyLength);
        Span<byte> branch = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(branch, prefix.BitCount, node.LeftHash, node.RightHash);
        prefix.Bytes.CopyTo(branch[3..]);
        Span<byte> trailer = branch[PbtNodeCodec.BranchPreimageLength(prefix.BitCount)..];
        PbtNodeCodec.WriteBranchTrailer(trailer, leftKeyLength, rightKeyLength);
        if (node.HasLeftLeaf) leftKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
        if (node.HasRightLeaf) rightKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftKeyLength)..]);
        bool hashKnown = node.KnownHash != default && prefix.BitCount == node.KnownHashBitCount;
        return new(offset, length, hashKnown ? node.KnownHash : default);
    }

    /// <summary>Appends a stored branch anchored <paramref name="skippedBits"/> above <paramref name="position"/>, with the rest of its compressed prefix.</summary>
    private static ComposedNode AppendReanchored(PbtNodeGroupWriter<TPath> writer, int position, int skippedBits, PbtNodeReader stored)
    {
        int offset = writer.WrittenCount;
        int bitCount = stored.Prefix.BitCount - skippedBits;
        int length = PbtNodeCodec.BranchLength(bitCount, stored.LeftKey.Length, stored.RightKey.Length);
        Span<byte> branch = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(branch, bitCount, stored.LeftHash, stored.RightHash);
        PbtBitPrefix.CopyBits(stored.Prefix.Bytes, skippedBits, bitCount, branch[3..], 0);
        PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(bitCount)..], stored.LeftKey, stored.RightKey);
        return new(offset, length, default);
    }

    /// <summary>Appends the branch at <paramref name="position"/> that the group leaves implicit, rebuilt from the children it stores.</summary>
    /// <remarks>
    /// Only an interior prefixless branch is ever left out (<see cref="PbtNodeGroupCodec.ShouldOmit"/>), so a
    /// boundary position or the root with nothing stored, or a child missing, is a corrupt group. The link hash
    /// decomposition seeded is kept, so the branch is only hashed again if a sibling's deletion promotes it. With that
    /// hash known and the branch left out again, its child hashes are only needed by <see cref="Rise"/>, so they are
    /// resolved there instead, sparing the rehash of every unchanged node below it.
    /// </remarks>
    private static ComposedNode AppendImplicitBranch(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        int position)
    {
        int width = PbtFourLevelGroupGeometry.WidthOf(position);
        if (width is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
        {
            int offset = writer.WrittenCount;
            int length = PbtNodeCodec.BranchLength(0, 0, 0);
            Span<byte> branch = writer.Append(position, length);
            ValueHash256 seeded = reader.SeededHash(position);
            if (seeded != default)
            {
                // The seeded hash only stands in for the child hashes, which omission does not look at.
                PbtNodeCodec.CreateBranchEncoding(branch, 0, seeded, seeded);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                if (writer.Omits(position, branch)) return new(offset, length, seeded) { ChildHashesPending = true };
            }

            reader.GetChildHashes(path, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
            if (left != default && right != default)
            {
                PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                return new(offset, length, seeded);
            }
        }
        throw new InvalidDataException("A referenced PBT node is missing.");
    }

    internal static void SetBoundary(ref Frontier frontier, scoped Span<FoldResult> results, int slot, ref FoldResult result)
    {
        uint bit = 1u << BoundaryPosition(slot);
        frontier.Mask = result.IsEmpty ? frontier.Mask & ~bit : frontier.Mask | bit;
        frontier.Set(results, slot, ref result);
    }

    /// <summary>Rebuilds the group from the frontier into <paramref name="writer"/>, returning its root for the caller to place.</summary>
    /// <remarks>
    /// The walk is post-order, and the writer's buffer is its stack memory: every node is appended at its own position
    /// as soon as it is produced, and what moves up the walk is only where it sits. The parent reads it back from there.
    /// A node that rises over an empty sibling is always the last entry, so it is rewritten in place one level up; a
    /// node that must not stay in the group, an inlined leaf or an omitted branch, is dropped as soon as its position is
    /// settled, while it is still the last entry, keeping only what its parent needs. The root is returned rather than
    /// kept, anchored at <paramref name="resultDepth"/>.
    /// </remarks>
    internal static FoldResult Compose(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int resultDepth,
        TrieUpdaterMetrics? metrics, scoped ref Frontier frontier, scoped Span<FoldResult> results)
    {
        uint copies = frontier.Copies;
        uint frontierMask = frontier.Mask;
        writer.ReserveFirstBuffer(reader.PayloadLength);
        ComposeFrameBuffer frames = default;
        int frameCount = 1;
        ComposedNode prevSubtree = default;
        while (frameCount != 0)
        {
            // The top of the stack: the position being visited, which is prevSubtree's parent when one of its children has just completed.
            ref ComposeFrame frame = ref frames[frameCount - 1];
            int position = frame.Path.Position;
            if (frame.Stage == ComposeStage.Descend)
            {
                // First visit, going down. A position is held only when no slot under it was touched: a direct copy,
                // or a frontier entry (a boundary node, a fold's result, or an untouched block). Its node is then the
                // whole subtree, returned up as it is. A node the group stores above a touched slot is never held, so
                // the walk goes down past it and rebuilds it from its children.
                if ((copies & (1u << position)) != 0)
                {
                    // A direct copy is stored with its descendants as one contiguous range, ending at the node itself.
                    int copied = reader.CopyRange(path, writer, position - 2 * frame.Path.Width + 2, position + 1);
                    metrics?.AddBulkCopy(copied);
                    int length = reader.GetEncoding(path, position).Length;
                    prevSubtree = new ComposedNode(writer.WrittenCount - length, length, reader.SeededHash(position));
                    frameCount--;
                    continue;
                }
                if ((frontierMask & (1u << position)) != 0)
                {
                    prevSubtree = AppendHeld(ref reader, writer, path, ref frontier, results, position, metrics);
                    frameCount--;
                    continue;
                }
                // A boundary slot has no children to go down into, so it returns up empty.
                if (frame.Path.Length == PbtFourLevelGroupGeometry.LevelsPerGroup)
                {
                    prevSubtree = default;
                    frameCount--;
                    continue;
                }

                // Nothing is held here, so keep going down the left child.
                frame.Stage = ComposeStage.AwaitingLeft;
                frames[frameCount] = new(frame.Path.Left);
                frameCount++;
                continue;
            }
            if (frame.Stage == ComposeStage.AwaitingLeft)
            {
                // Back up from the left child, whose node is in prevSubtree. With no right child, that node rises to
                // this position. Otherwise the walk goes down the right child, after settling the left node; with no
                // left node, the right one rises here instead.
                uint rightMask = ((1u << (frame.Path.Width - 1)) - 1) << (position - frame.Path.Width + 1);
                if (((frontierMask | copies) & rightMask) == 0)
                {
                    if (!prevSubtree.IsEmpty) prevSubtree = Rise(ref reader, writer, path, prevSubtree, position - frame.Path.Width, position, 0);
                    frameCount--;
                    continue;
                }

                if (prevSubtree.IsEmpty)
                {
                    // With no left node, the right child's node rises to this position.
                    frame.Stage = ComposeStage.AwaitingOnlyRight;
                }
                else
                {
                    SettleLeft(writer, path, position - frame.Path.Width, prevSubtree, ref frame, metrics);
                    frame.Stage = ComposeStage.AwaitingRight;
                }
                frames[frameCount] = new(frame.Path.Right);
                frameCount++;
                continue;
            }
            if (frame.Stage == ComposeStage.AwaitingOnlyRight)
            {
                // Back up from the right child of a position with no left node: its node rises to this position.
                prevSubtree = Rise(ref reader, writer, path, prevSubtree, position - 1, position, 1);
                frameCount--;
                continue;
            }
            if (frame.Stage == ComposeStage.AwaitingRight)
            {
                // Back up from the right child with both children present: settle the right one and append the branch
                // over the two, returning it up to the parent frame.
                prevSubtree = AppendBranch(writer, path, position, prevSubtree, ref frame, metrics);
                frameCount--;
                continue;
            }
        }
        return TakeRoot(writer, path, resultDepth, prevSubtree);
    }

    /// <summary>Moves the last entry, the node at <paramref name="childPosition"/> on <paramref name="side"/> of <paramref name="position"/>, up to <paramref name="position"/>.</summary>
    /// <remarks>
    /// A branch gains the side bit in front of its compressed prefix, and so needs hashing again; a leaf's encoding does
    /// not depend on its position. An implicit branch appended without its child hashes has them resolved here.
    /// </remarks>
    [SkipLocalsInit]
    private static ComposedNode Rise(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        in ComposedNode node, int childPosition, int position, int side)
    {
        // The entry is rewritten over itself, so it is read from a copy.
        Span<byte> previous = stackalloc byte[node.Length];
        writer.Entry(node.Offset, node.Length).Span.CopyTo(previous);
        writer.DropLast(childPosition);
        PbtNodeReader stored = PbtNodeReader.FromValidated(previous);
        if (stored.IsLeaf)
        {
            previous.CopyTo(writer.Append(position, node.Length));
            return node;
        }

        ValueHash256 leftHash = stored.LeftHash;
        ValueHash256 rightHash = stored.RightHash;
        if (node.ChildHashesPending)
        {
            int width = PbtFourLevelGroupGeometry.WidthOf(childPosition);
            reader.GetChildHashes(path, childPosition - width, childPosition - 1, out leftHash, out rightHash);
        }
        CompressedPrefix prefix = stored.Prefix;
        int bitCount = prefix.BitCount + 1;
        int length = PbtNodeCodec.BranchLength(bitCount, stored.LeftKey.Length, stored.RightKey.Length);
        Span<byte> encoding = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, leftHash, rightHash);
        encoding[3] |= (byte)(side << 7);
        PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, encoding[3..], 1);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..], stored.LeftKey, stored.RightKey);
        return new(node.Offset, length, default);
    }

    /// <summary>Settles the left child at <paramref name="leftPosition"/> into <paramref name="frame"/>, dropping it when the group does not keep it.</summary>
    /// <remarks>
    /// A leaf is inlined into the branch above it, so only its key and hash are kept. An omitted branch is hashed now,
    /// while it is still the last entry, since the right subtree is written over it. A kept branch stays in the
    /// writer, its preimage read back from there to be hashed together with its sibling's.
    /// </remarks>
    private static void SettleLeft(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int leftPosition, in ComposedNode left, ref ComposeFrame frame,
        TrieUpdaterMetrics? metrics)
    {
        ReadOnlySpan<byte> encoding = writer.Entry(left.Offset, left.Length).Span;
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
        frame.LeftIsLeaf = node.IsLeaf;
        frame.LeftHash = left.Hash;
        frame.LeftPreimageLength = 0;
        if (node.IsLeaf)
        {
            frame.LeftKey = TKey.Create(node.Key);
            writer.DropLast(leftPosition);
            return;
        }
        if (writer.Omits(leftPosition, encoding))
        {
            if (frame.LeftHash == default)
            {
                metrics?.IncrementNodeHashes();
                frame.LeftHash = Blake3Hash.Hash(node.Preimage);
            }
            writer.DropLast(leftPosition);
            return;
        }
        writer.ValidateEntry(path, leftPosition, encoding);
        if (frame.LeftHash == default)
        {
            frame.LeftPreimageOffset = left.Offset;
            frame.LeftPreimageLength = node.Preimage.Length;
        }
    }

    /// <summary>Settles the right child, the last entry, and appends the branch over it and the frame's left child at <paramref name="position"/>.</summary>
    [SkipLocalsInit]
    private static ComposedNode AppendBranch(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int position, in ComposedNode right, ref ComposeFrame frame,
        TrieUpdaterMetrics? metrics)
    {
        int rightPosition = position - 1;
        ReadOnlySpan<byte> encoding = writer.Entry(right.Offset, right.Length).Span;
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
        bool rightIsLeaf = node.IsLeaf;
        TKey rightKey = rightIsLeaf ? TKey.Create(node.Key) : default;
        ValueHash256 rightHash = right.Hash;
        ReadOnlySpan<byte> rightPreimage = rightHash == default ? node.Preimage : default;
        ReadOnlySpan<byte> leftPreimage = frame.LeftPreimageLength == 0 ? default : writer.Entry(frame.LeftPreimageOffset, frame.LeftPreimageLength).Span;
        HashPending(leftPreimage, ref frame.LeftHash, rightPreimage, ref rightHash, metrics);
        if (rightIsLeaf || writer.Omits(rightPosition, encoding))
            writer.DropLast(rightPosition);
        else
            writer.ValidateEntry(path, rightPosition, encoding);

        int leftKeyLength = frame.LeftIsLeaf ? frame.LeftKey.Length : 0;
        int rightKeyLength = rightIsLeaf ? rightKey.Length : 0;
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.BranchLength(0, leftKeyLength, rightKeyLength);
        Span<byte> branch = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(branch, 0, frame.LeftHash, rightHash);
        Span<byte> trailer = branch[PbtNodeCodec.BranchPreimageLength(0)..];
        PbtNodeCodec.WriteBranchTrailer(trailer, leftKeyLength, rightKeyLength);
        if (frame.LeftIsLeaf) frame.LeftKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
        if (rightIsLeaf) rightKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftKeyLength)..]);
        return new(offset, length, default);
    }

    /// <summary>Detaches the group's root, the last entry, as a result anchored at <paramref name="resultDepth"/>, and drops it from the group.</summary>
    private static FoldResult TakeRoot(PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int resultDepth, in ComposedNode root)
    {
        if (root.IsEmpty) return default;
        PbtNodeReader node = PbtNodeReader.FromValidated(writer.Entry(root.Offset, root.Length).Span);
        FoldResult result = node.IsLeaf ? new(TKey.Create(node.Key), root.Hash) : ReanchoredRoot(path, resultDepth, node);
        writer.DropLast(PbtFourLevelGroupGeometry.RootPosition);
        return result;
    }

    /// <summary>A group's root branch, stored at the group's depth, as a result anchored at <paramref name="resultDepth"/>.</summary>
    /// <remarks>A prefix jump leaves <paramref name="resultDepth"/> above the group, so the bits in between are read from <paramref name="path"/>.</remarks>
    private static FoldResult ReanchoredRoot(scoped in PbtTraversalPath path, int resultDepth, scoped in PbtNodeReader root)
    {
        CompressedPrefix prefix = root.Prefix;
        int anchorDepth = path.BitDepth;
        int splitDepth = anchorDepth + prefix.BitCount;
        int localLength = Math.Min(splitDepth - resultDepth, PbtFourLevelGroupGeometry.LevelsPerGroup);
        int slot = 0;
        for (int bit = resultDepth; bit < resultDepth + localLength; bit++)
            slot = (slot << 1) | (bit < anchorDepth ? GetBit(path.Bytes, bit) : GetBit(prefix.Bytes, bit - anchorDepth));

        int ownedStart = resultDepth + localLength;
        scoped Span<byte> ownedPrefix = default;
        if (ownedStart < splitDepth)
        {
            ownedPrefix = stackalloc byte[sizeof(ushort) + PbtBitPrefix.ByteCount(splitDepth - ownedStart)];
            // Zeroed, because the bits are copied in by disjunction.
            ownedPrefix.Clear();
            BinaryPrimitives.WriteUInt16BigEndian(ownedPrefix, (ushort)(splitDepth - ownedStart));
            Span<byte> bits = ownedPrefix[sizeof(ushort)..];
            if (ownedStart < anchorDepth) PbtBitPrefix.CopyBits(path.Bytes, ownedStart, anchorDepth - ownedStart, bits, 0);
            int prefixStart = Math.Max(ownedStart, anchorDepth);
            if (prefixStart < splitDepth) PbtBitPrefix.CopyBits(prefix.Bytes, prefixStart - anchorDepth, splitDepth - prefixStart, bits, prefixStart - ownedStart);
        }

        return new(new NodeGroupPath(slot << (PbtFourLevelGroupGeometry.LevelsPerGroup - localLength), localLength),
            root.LeftHash, root.RightHash,
            root.LeftKey.IsEmpty ? default : TKey.Create(root.LeftKey),
            root.RightKey.IsEmpty ? default : TKey.Create(root.RightKey),
            (byte)((root.LeftKey.IsEmpty ? 0 : Subtree.LeftLeaf) | (root.RightKey.IsEmpty ? 0 : Subtree.RightLeaf)), ownedPrefix);
    }

    /// <summary>Hashes the sibling preimages still pending, together when both are.</summary>
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

    /// <summary>What a frame waits for: set before its child is pushed, and handled once that child returns up.</summary>
    private enum ComposeStage : byte { Descend, AwaitingLeft, AwaitingRight, AwaitingOnlyRight }

    private struct ComposeFrame(NodeGroupPath path)
    {
        internal NodeGroupPath Path = path;
        internal ComposeStage Stage;
        internal ValueHash256 LeftHash;
        /// <summary>Where the left child's preimage awaiting its sibling sits in the writer.</summary>
        internal int LeftPreimageOffset;
        /// <summary>The length of the left child's preimage awaiting its sibling, or zero when its hash is already known.</summary>
        internal int LeftPreimageLength;
        internal TKey LeftKey;
        internal bool LeftIsLeaf;
    }

    /// <summary>A node composition has appended to the writer, read back from there by its parent.</summary>
    internal readonly struct ComposedNode(int offset, int length, in ValueHash256 hash)
    {
        internal readonly int Offset = offset;
        internal readonly int Length = length;
        /// <summary>The node's hash, or default while its preimage waits in the writer to be hashed with its sibling's.</summary>
        internal readonly ValueHash256 Hash = hash;
        /// <summary>Whether the encoding is an omitted implicit branch whose child hashes were not resolved, which only <see cref="Rise"/> needs.</summary>
        internal bool ChildHashesPending { get; init; }
        internal bool IsEmpty => Length == 0;
    }

    [InlineArray(PbtFourLevelGroupGeometry.LevelsPerGroup + 1)]
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
        frontier.Copies = copies;
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
