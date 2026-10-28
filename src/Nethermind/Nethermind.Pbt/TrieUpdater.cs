// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Metric;
using Nethermind.Core.Threading;
using Nethermind.Pbt.NodeBuffer;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static partial class TrieUpdater
{
    public static int GetBit(ReadOnlySpan<byte> bytes, int bit) => (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;

    /// <summary>The bit flagging a composed branch's left child as a leaf.</summary>
    public const byte LeftLeaf = 1;
    /// <summary>The bit flagging a composed branch's right child as a leaf.</summary>
    public const byte RightLeaf = 2;

    /// <summary>Where a boundary node's complete key is read from, which is also whether it is a leaf at all.</summary>
    public enum LeafSource : byte
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

    /// <summary>Runs <paramref name="work"/> with <paramref name="state"/> for every index below <paramref name="count"/>, across threads as <paramref name="quota"/> allows.</summary>
    /// <remarks>
    /// Work runs on the calling thread while the quota has no free slot; the first slot taken admits a parallel loop over
    /// the indices still left, whose workers charge themselves as they start. That admission slot is claimed by the first
    /// worker thread to start, through <c>admissionSlotClaimed</c>, or returned after the loop when none did; every further
    /// worker takes quota outright, so the loop is charged exactly once per running worker, briefly exceeding the budget
    /// when sibling loops start together. The calling thread already holds its own slot and is not charged.
    /// </remarks>
    public static void ForEachOnQuota<TState>(ConcurrencyController quota, int count, TState state, Action<int, TState> work)
    {
        int next = 0;
        for (; next < count - 1 && !quota.TryRequestConcurrencyQuota(); next++)
            work(next, state);
        if (next < count - 1)
        {
            ForEachAdmitted(quota, next, count, state, work);
        }
        else if (next < count)
        {
            work(next, state);
        }
    }

    /// <summary>The parallel loop of <see cref="ForEachOnQuota"/> once its admission slot is taken.</summary>
    /// <remarks>Kept apart so the closure over the loop's state is allocated only once a loop goes parallel.</remarks>
    private static void ForEachAdmitted<TState>(ConcurrencyController quota, int next, int count, TState state, Action<int, TState> work)
    {
        int callerThreadId = Environment.CurrentManagedThreadId;
        int admissionSlotClaimed = 0;
        try
        {
            ParallelUnbalancedWork.For(next, count, ParallelUnbalancedWork.DefaultOptions,
                () =>
                {
                    if (Environment.CurrentManagedThreadId == callerThreadId) return false;
                    if (Interlocked.Exchange(ref admissionSlotClaimed, 1) != 0) quota.TakeConcurrencyQuota();
                    return true;
                },
                (index, tookQuota) =>
                {
                    work(index, state);
                    return tookQuota;
                },
                tookQuota =>
                {
                    if (tookQuota) quota.ReturnConcurrencyQuota();
                });
        }
        finally
        {
            if (Interlocked.Exchange(ref admissionSlotClaimed, 1) == 0) quota.ReturnConcurrencyQuota();
        }
    }

    /// <summary>Reads the boundary slot of the group at <paramref name="groupDepth"/> that <paramref name="key"/> lies under.</summary>
    /// <remarks>The slot can span two key bytes; bits past the key's end read as zero.</remarks>
    public static int BoundarySlot(ReadOnlySpan<byte> key, int groupDepth)
    {
        int index = groupDepth >> 3;
        int window = key[index] << 8;
        if (index + 1 < key.Length) window |= key[index + 1];
        return (window >> (16 - PbtThreeLevelGroupGeometry.LevelsPerGroup - (groupDepth & 7))) & (PbtThreeLevelGroupGeometry.BoundarySlots - 1);
    }

}

public static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>Publishes a frame's group and returns the size change of the group and everything folded below it.</summary>
    /// <remarks>
    /// Each stored descendant size is the loaded (or inherited) size of its slot adjusted by the change folded below that
    /// slot. A group that is or becomes absent writes no record, so only the folded change survives. A written group is
    /// pending in the working state, with a pending pointer and its writer, until <see cref="FoldWorkingState.Commit"/> writes it to
    /// the store. The working state takes the writer either way.
    /// </remarks>
    public readonly record struct GroupPublication(long SizeDelta, ulong Pointer);

    public static GroupPublication PublishGroup<TFrame>(FoldContext context, ref TFrame reader, PbtNodeGroupWriter<TPath> writer,
        scoped in PbtTraversalPath path) where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Span<long> descendantBytes = stackalloc long[PbtThreeLevelGroupGeometry.BoundarySlots];
        ushort candidateSlots = (ushort)(reader.DescendantMask | writer.DescendantDeltaMask);
        long deltaTotal = 0;
        PbtGroupLinks links = writer.DescendantPointers;
        // Only an absent group uses this link; its descendants follow a single compressed route.
        ulong surviving = 0;
        for (uint remaining = candidateSlots; remaining != 0; remaining &= remaining - 1)
        {
            int slot = BitOperations.TrailingZeroCount(remaining);
            long delta = writer.DescendantDelta(slot);
            descendantBytes[slot] = reader.DescendantBytes(slot) + delta;
            deltaTotal += delta;
            if (descendantBytes[slot] == 0) links[slot] = 0;
            else
            {
                surviving = links[slot];
                // A committed record drops the warmed groups' transient links, which do not outlive the block.
                links[slot] = PbtGroupRecordCodec.PersistentPointer(surviving);
            }
        }
        int length = writer.GetPayloadLength(descendantBytes, candidateSlots);
        ulong pointer = surviving;
        if (length != 0) pointer = context.WorkingState.AddPendingGroup(writer, path.ToPath<PbtStorageNodePath>(), links, descendantBytes, candidateSlots, length);
        else context.WorkingState.ReturnWriter(writer);
        return new GroupPublication(length - reader.PayloadLength + deltaTotal, pointer);
    }

    private static void InitializeDescendantPointers<TFrame>(ref TFrame reader, PbtNodeGroupWriter<TPath> writer)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        for (uint remaining = reader.DescendantMask; remaining != 0; remaining &= remaining - 1)
        {
            int slot = BitOperations.TrailingZeroCount(remaining);
            writer.DescendantPointers[slot] = reader.DescendantPointer(slot);
        }
    }

    /// <summary>Whether <paramref name="current"/>'s whole subtree is the node itself, which its owner group stores.</summary>
    /// <remarks>
    /// A group holds the nodes strictly below its boundary node. Nothing is stored below an empty subtree or a leaf,
    /// and a branch over two inlined leaves is its own whole subtree, so none of the three owns a group.
    /// LeafChildrenMask decodes the branch encoding, so the empty and leaf kinds are ruled out first.
    /// </remarks>
    private static bool OwnsNoGroup(scoped in BoundaryNode current) =>
        current.IsEmpty || current.IsLeaf || current.LeafChildrenMask == (LeftLeaf | RightLeaf);

    /// <summary>Whether <paramref name="current"/> is a branch whose children lie beyond the group at <paramref name="bitDepth"/>, so that group cannot exist.</summary>
    private static bool IsAbsentGroupBelow(scoped in BoundaryNode current, int bitDepth)
    {
        Debug.Assert(bitDepth != 0, "The root group always exists.");
        return !current.IsEmpty && !current.IsLeaf && current.BranchDepth >= bitDepth + PbtThreeLevelGroupGeometry.LevelsPerGroup;
    }

    /// <summary>Per-fold state shared by every frame of one root update, with the working state of the thread folding.</summary>
    /// <remarks>
    /// <see cref="FoldQuota"/> is the budget every nested frame takes its extra workers from before fanning out;
    /// <see cref="Operations"/>, set only when wide frames may fold their buckets concurrently, is the backing array of
    /// every operation range, so a bucket can rebuild its span on another thread,
    /// and <see cref="FanOut"/> gives how many operations a concurrent run of buckets holds at least, by what it has stored below it.
    /// Buffer frames borrow records directly; <see cref="Store"/> remains the exact-view fallback and takes every group record.
    /// A parallel run folds under a context of its own, carrying a working state from <see cref="Spares"/>, which is set with <see cref="Operations"/>.
    /// Commits are timed on <see cref="PhaseTime"/>, when set, under <see cref="PhaseLabels"/>.
    /// </remarks>
    public readonly struct FoldContext(IPbtStore store, ConcurrencyController foldQuota, PbtWriteOperation<TKey>[]? operations,
        FoldFanOut fanOut, FoldWorkingState workingState, SpareWorkingStates? spares, IMetricObserver? phaseTime, FoldPhaseLabels? phaseLabels)
    {
        public IPbtStore Store { get; } = store;
        public ConcurrencyController FoldQuota { get; } = foldQuota;
        public PbtWriteOperation<TKey>[]? Operations { get; } = operations;
        public FoldFanOut FanOut { get; } = fanOut;
        public FoldWorkingState WorkingState { get; } = workingState;
        public SpareWorkingStates? Spares { get; } = spares;
        public IMetricObserver? PhaseTime { get; } = phaseTime;
        public FoldPhaseLabels? PhaseLabels { get; } = phaseLabels;

        /// <summary>Commits the working state's pending groups from <paramref name="top"/>, as <see cref="FoldWorkingState.Commit"/> does, observing the time as the commit phase.</summary>
        public ulong Commit(ulong top)
        {
            if (PhaseTime is null) return WorkingState.Commit(Store, top);

            long start = Stopwatch.GetTimestamp();
            ulong pointer = WorkingState.Commit(Store, top);
            PhaseTime.Observe(Stopwatch.GetTimestamp() - start, PhaseLabels!.Commit);
            return pointer;
        }
    }
}
