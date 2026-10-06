// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Threading;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static partial class TrieUpdater
{
    internal static int GetBit(ReadOnlySpan<byte> bytes, int bit) => (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;

    internal enum NodeKind : byte { Empty, Leaf, Branch }

    /// <summary>The bit flagging a composed branch's left child as a leaf.</summary>
    internal const byte LeftLeaf = 1;
    /// <summary>The bit flagging a composed branch's right child as a leaf.</summary>
    internal const byte RightLeaf = 2;

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
    /// <summary>Publishes a frame's group and returns the size change of the group and everything folded below it.</summary>
    /// <remarks>
    /// Each stored descendant size is the loaded (or inherited) size of its slot adjusted by the change folded below that
    /// slot. A group that stays absent stores nothing, so only the folded change survives, and the store is not told
    /// to delete a group it never held.
    /// </remarks>
    internal static long PublishGroup<TFrame>(IPbtNodeGroupSink sink, ref TFrame reader, PbtNodeGroupWriter<TPath> writer,
        scoped in PbtTraversalPath path, in ValueHash256 hash)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Span<long> descendantBytes = stackalloc long[PbtNodeGroupCodec.DescendantSlots];
        ushort candidateSlots = (ushort)(reader.DescendantMask | writer.DescendantDeltaMask);
        long deltaTotal = 0;
        for (uint remaining = candidateSlots; remaining != 0; remaining &= remaining - 1)
        {
            int slot = BitOperations.TrailingZeroCount(remaining);
            long delta = writer.DescendantDelta(slot);
            descendantBytes[slot] = reader.DescendantBytes(slot) + delta;
            deltaTotal += delta;
        }
        using RefCountingMemory? payload = writer.Detach(descendantBytes, candidateSlots);
        if (payload is not null || reader.PayloadLength != 0) sink.SetNodeGroup(path, hash, payload);
        return (payload?.GetSpan().Length ?? 0) - reader.PayloadLength + deltaTotal;
    }

    /// <summary>Whether the group at <paramref name="bitDepth"/> below <paramref name="current"/> is provably absent, so it is folded without reading the store.</summary>
    /// <remarks>
    /// A group holds the nodes strictly below its boundary node. Nothing is stored below an empty subtree or a leaf,
    /// and a branch over two inlined leaves is its own whole subtree, so none of the three owns a group. A branch whose
    /// children lie beyond this group owns no group either. Any other branch has a child inside the group, which is stored.
    /// </remarks>
    internal static bool IsAbsentGroup(scoped in BoundaryNode current, int bitDepth) => OwnsNoGroup(current) || IsAbsentGroupBelow(current, bitDepth);

    /// <summary>The frame of the absent group at <paramref name="bitDepth"/> below <paramref name="current"/>.</summary>
    /// <param name="spanningDescendantBytes">
    /// The size the owner recorded under the boundary slot on the way here, which a spanning branch carries down with it.
    /// </param>
    internal static AbsentGroupFrame<TKey, TPath> AbsentFrame(scoped in BoundaryNode current, scoped in PbtTraversalPath path, int bitDepth,
        long spanningDescendantBytes) =>
        // A spanning branch may inline two leaves as well; taking its slot size keeps the owner's accounting.
        IsAbsentGroupBelow(current, bitDepth)
            ? new(bitDepth, current.BranchSlot(path, bitDepth), spanningDescendantBytes)
            : new(bitDepth);

    /// <summary>Whether <paramref name="current"/>'s whole subtree is the node itself, which its owner group stores.</summary>
    /// <remarks>LeafChildrenMask decodes the branch encoding, so the empty and leaf kinds are ruled out first.</remarks>
    private static bool OwnsNoGroup(scoped in BoundaryNode current) =>
        current.IsEmpty || current.IsLeaf || current.LeafChildrenMask == (LeftLeaf | RightLeaf);

    /// <summary>Whether <paramref name="current"/> is a branch whose children lie beyond the group at <paramref name="bitDepth"/>, so that group cannot exist.</summary>
    internal static bool IsAbsentGroupBelow(scoped in BoundaryNode current, int bitDepth)
    {
        Debug.Assert(bitDepth != 0, "The root group always exists.");
        return !current.IsEmpty && !current.IsLeaf && current.BranchDepth >= bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    }

    /// <summary>Per-fold state shared by every frame of one root update.</summary>
    /// <remarks>
    /// <see cref="FoldQuota"/> and <see cref="Operations"/> are set only when wide frames may fold their buckets
    /// concurrently; the former is the budget every nested frame takes its extra workers from before fanning out, the latter
    /// is the backing array of every operation range, so a bucket can rebuild its span on another thread,
    /// and <see cref="FanOut"/> gives how many operations a concurrent run of buckets holds at least, by what it has stored below it.
    /// Groups are read from <see cref="Store"/> but published to <see cref="Writer"/>, which a concurrent bucket replaces
    /// with its own <see cref="IPbtStore.CreateWriter"/> so it never writes the store directly.
    /// </remarks>
    internal sealed class FoldContext(IPbtStore store, IPbtNodeGroupSink writer, IRefCountingMemoryProvider memoryProvider, ConcurrencyController? foldQuota, PbtWriteOperation<TKey>[]? operations, FoldFanOut fanOut)
    {
        internal IPbtStore Store { get; } = store;
        internal IPbtNodeGroupSink Writer { get; } = writer;
        internal IRefCountingMemoryProvider MemoryProvider { get; } = memoryProvider;
        internal ConcurrencyController? FoldQuota { get; } = foldQuota;
        internal PbtWriteOperation<TKey>[]? Operations { get; } = operations;
        internal FoldFanOut FanOut { get; } = fanOut;
    }
}
