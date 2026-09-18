// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    public static readonly int MinEntriesToParallelizeThreshold =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_BULKSET_MIN_ENTRIES_PER_WORKER"), out int minEntriesPerWorker) ? minEntriesPerWorker : 32;
    private const int InPlaceSortThreshold = 32;
    private const int BSearchThreshold = 128;

    [Flags]
    public enum Flags
    {
        None = 0,
        WasSorted = 1,
        DoNotParallelize = 2
    }

    public readonly struct BulkSetEntry(in ValueHash256 path, byte[]? value) : IComparable<BulkSetEntry>
    {
        public readonly ValueHash256 Path = path;
        public readonly byte[]? Value = value;

        public int CompareTo(BulkSetEntry entry) => Path.CompareTo(entry.Path);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetPathNibble(int index)
        {
            int offset = index / 2;
            Span<byte> theSpan = Path.BytesAsSpan;
            int b = theSpan[offset];

            return (index & 1) == 0
                ? (byte)((b & 0xf0) >> 4)
                : (byte)(b & 0x0f);
        }
    }

    /// <summary>
    /// BulkSet multiple entries at the same time. It works by working each nibble level one at a time, partially
    /// sorting the <see cref="entries"/> then recurs on each nibble, traversing the top level branch only once.
    /// if <see cref="Flags.WasSorted"/> is on, the sort is skipped for a slightly faster set.
    /// A level forks its buckets onto the <see cref="Rayon"/> pool, at any depth, as long as each side of a
    /// fork carries at least <see cref="MinEntriesToParallelizeThreshold"/> entries.
    /// </summary>
    /// <param name="entries"></param>
    /// <param name="flags"></param>
    public void BulkSet(in ArrayPoolListRef<BulkSetEntry> entries, Flags flags = Flags.None)
    {
        if (entries.Count == 0)
            return;
#if ZK_EVM
        flags |= Flags.DoNotParallelize;
#endif

        TreePath path = TreePath.Empty;

        // Small-batch fast path: skip the ArrayPool<BulkSetEntry>.Rent of a sort buffer and
        // reuse the entries array for both spans, since InPlaceBucketSort16 / SortTiny sort
        // in place without needing a separate target.
        if (entries.Count < InPlaceSortThreshold)
        {
            Context ctx = new()
            {
                OriginalEntriesArray = entries.UnsafeGetInternalArray(),
                OriginalSortBufferArray = entries.UnsafeGetInternalArray(),
            };

            TrieNode? newRoot = BulkSet(
                ctx,
                entries.AsSpan(),
                entries.AsSpan(),
                ref path,
                RootRef,
                0,
                flags);
            RootRef = newRoot;
            _writeBeforeCommit += entries.Count;
            return;
        }

        // Large-batch path: BucketSort16 needs a separate sort target. Rent a buffer once here
        // and flip-flop entries/sortBuffer on each recursion level (see `flipCount` in the
        // recursive BulkSet).
        using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(entries.Count, entries.Count);

        Context ctx2 = new()
        {
            OriginalSortBufferArray = sortBuffer.UnsafeGetInternalArray(),
            OriginalEntriesArray = entries.UnsafeGetInternalArray(),
        };

        TrieNode? newRoot2 = BulkSet(
            ctx2,
            entries.AsSpan(),
            sortBuffer.AsSpan(),
            ref path,
            RootRef,
            0,
            flags);
        RootRef = newRoot2;

        _writeBeforeCommit += entries.Count;
    }

    private readonly record struct Context(BulkSetEntry[] OriginalEntriesArray, BulkSetEntry[] OriginalSortBufferArray);

    /// <param name="ctx">Just to reduce the param count</param>
    /// <param name="entries">The entries</param>
    /// <param name="sortBuffer">Entry buffer used during sort. May be flipped between this and `entries` on recursion.</param>
    /// <param name="path"></param>
    /// <param name="node"></param>
    /// <param name="flipCount">Flip count, for parallelism.</param>
    /// <param name="flags"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private TrieNode? BulkSet(
        in Context ctx,
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> sortBuffer,
        ref TreePath path,
        TrieNode? node,
        int flipCount,
        Flags flags)
    {
        TrieNode? originalNode = node;

        if (entries.Length == 1)
            return BulkSetOne(in entries[0], ref path, node);

        bool newBranch = false;

        if (node is null)
        {
            node = TrieNodeFactory.CreateBranch();
            newBranch = true;
        }
        else
        {
            node.ResolveNode(TrieStore, path);

            if (!node.IsBranch)
            {
                // .1% of execution go here.
                node = MakeFakeBranch(ref path, node);
                newBranch = true;
            }
        }

        Span<int> indexes = stackalloc int[TrieNode.BranchesCount];

        if (path.Length == 64)
            throw new InvalidOperationException("Non unique entry keys");

        int nibMask;

        if (entries.Length <= 3)
        {
            nibMask = SortTiny(entries, path.Length, indexes);
        }
        else if ((flags & Flags.WasSorted) != 0)
        {
            nibMask = HexarySearchAlreadySorted(entries, path.Length, indexes);
        }
        else if (entries.Length < InPlaceSortThreshold)
        {
            nibMask = InPlaceBucketSort16(entries, path.Length, indexes);
        }
        else
        {
            nibMask = BucketSort16(entries, sortBuffer, path.Length, indexes);
            // Buffer is now partially sorted. Swap buffer and entries
            flipCount++;

            Span<BulkSetEntry> newBufferSpan = entries;
            entries = sortBuffer;
            sortBuffer = newBufferSpan;
        }

        bool hasRemove = false;
        int nonNullChildCount = 0;

        if (!Core.Cpu.RuntimeInformation.IsSingleProcessor
            && entries.Length >= 2 * MinEntriesToParallelizeThreshold
            && (nibMask & (nibMask - 1)) != 0
            && !flags.HasFlag(Flags.DoNotParallelize))
        {
            using ArrayPoolList<BulkSetJob> jobs = new(TrieNode.BranchesCount, TrieNode.BranchesCount);

            BulkSetEntry[] entriesArray = (flipCount % 2 == 0) ? ctx.OriginalEntriesArray : ctx.OriginalSortBufferArray;
            TrieNode.ChildIterator childIterator = node.CreateChildIterator();

            int remainingMask = nibMask;
            while (remainingMask != 0)
            {
                int nib = BitOperations.TrailingZeroCount(remainingMask);
                remainingMask &= remainingMask - 1;
                int startRange = indexes[nib];
                int endRange = remainingMask != 0 ? indexes[BitOperations.TrailingZeroCount(remainingMask)] : entries.Length;

                TreePath childPath = path.Append(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(TrieStore, ref childPath, nib);
                jobs[nib] = new BulkSetJob(GetSpanOffset(entriesArray, entries.Slice(startRange, endRange - startRange)), endRange - startRange, childPath, child);
            }

            BulkSetForked(new BulkSetForkState(this, ctx, jobs.UnsafeGetInternalArray(), flipCount, flags, nibMask));

            remainingMask = nibMask;
            while (remainingMask != 0)
            {
                int nib = BitOperations.TrailingZeroCount(remainingMask);
                remainingMask &= remainingMask - 1;
                ref BulkSetJob job = ref jobs.GetRef(nib);

                if (!ShouldUpdateChild(originalNode, job.CurrentChild, job.NewChild)) continue;

                if (job.NewChild is null)
                    hasRemove = true;

                if (job.NewChild is not null)
                    nonNullChildCount++;

                if (node.IsSealed)
                    node = node.Clone();

                node.SetChild(nib, job.NewChild);
            }
        }
        else
        {
            TrieNode.ChildIterator childIterator = node.CreateChildIterator();
            path.AppendMut(0);

            while (nibMask != 0)
            {
                int nib = BitOperations.TrailingZeroCount(nibMask);
                nibMask &= nibMask - 1;
                int startRange = indexes[nib];

                path.SetLast(nib);
                TrieNode? child = childIterator.GetChildWithChildPath(TrieStore, ref path, nib);

                int endRange;

                if (nibMask != 0)
                    endRange = indexes[BitOperations.TrailingZeroCount(nibMask)];
                else
                    endRange = entries.Length;

                TrieNode? newChild = (endRange - startRange == 1)
                    ? BulkSetOne(entries[startRange], ref path, child)
                    : BulkSet(in ctx,
                        entries[startRange..endRange],
                        sortBuffer[startRange..endRange],
                        ref path,
                        child,
                        flipCount,
                        flags
                    );

                if (!ShouldUpdateChild(originalNode, child, newChild))
                    continue;

                if (newChild is null)
                    hasRemove = true;

                if (newChild is not null)
                    nonNullChildCount++;

                if (node.IsSealed)
                    node = node.Clone();

                node.SetChild(nib, newChild);
            }

            path.TruncateOne();
        }

        if (!hasRemove && nonNullChildCount == 0) return originalNode;

        if ((hasRemove || newBranch) && nonNullChildCount < 2)
            node = MaybeCombineNode(ref path, node, originalNode);

        return node;
    }

    private struct BulkSetJob(int startIdx, int count, TreePath childPath, TrieNode? currentChild)
    {
        public readonly int StartIdx = startIdx;
        public readonly int Count = count;
        public readonly TreePath ChildPath = childPath;
        public readonly TrieNode? CurrentChild = currentChild;
        public TrieNode? NewChild;
    }

    private readonly record struct BulkSetForkState(PatriciaTree Tree, Context Ctx, BulkSetJob[] Jobs, int FlipCount, Flags Flags, int Mask);

    /// <summary>
    /// Recursively splits the populated nibbles in <see cref="BulkSetForkState.Mask"/> by entry count with
    /// <see cref="Rayon.Join{TSa,TSb}"/>, so uneven buckets are balanced by stealing and each bucket may fork
    /// again at deeper levels. A side that would fall below <see cref="MinEntriesToParallelizeThreshold"/>
    /// entries is not worth a job of its own, so such a split is set sequentially instead.
    /// </summary>
    private static void BulkSetForked(in BulkSetForkState state)
    {
        int mask = state.Mask;
        int total = 0;
        for (int remaining = mask; remaining != 0; remaining &= remaining - 1)
        {
            total += state.Jobs[BitOperations.TrailingZeroCount(remaining)].Count;
        }

        // Lower half: buckets in nibble order until the next one would cross half of the entries.
        int lower = 0;
        int lowerCount = 0;
        for (int remaining = mask; remaining != 0; remaining &= remaining - 1)
        {
            int nib = BitOperations.TrailingZeroCount(remaining);
            int count = state.Jobs[nib].Count;
            if (lowerCount > 0 && lowerCount + count > total / 2)
                break;

            lower |= 1 << nib;
            lowerCount += count;
        }

        int upper = mask ^ lower;
        if (upper == 0 || lowerCount < MinEntriesToParallelizeThreshold || total - lowerCount < MinEntriesToParallelizeThreshold)
        {
            for (int remaining = mask; remaining != 0; remaining &= remaining - 1)
            {
                state.Tree.BulkSetBucket(in state, BitOperations.TrailingZeroCount(remaining));
            }

            return;
        }

        Rayon.Join(
            state with { Mask = lower }, static lowerHalf => BulkSetForked(in lowerHalf),
            state with { Mask = upper }, static upperHalf => BulkSetForked(in upperHalf));
    }

    private void BulkSetBucket(in BulkSetForkState state, int nib)
    {
        ref BulkSetJob job = ref state.Jobs[nib];
        BulkSetEntry[] entriesArray = (state.FlipCount % 2 == 0) ? state.Ctx.OriginalEntriesArray : state.Ctx.OriginalSortBufferArray;
        BulkSetEntry[] bufferArray = (state.FlipCount % 2 == 0) ? state.Ctx.OriginalSortBufferArray : state.Ctx.OriginalEntriesArray;
        TreePath childPath = job.ChildPath;
        Context ctx = state.Ctx;
        job.NewChild = BulkSet(
            in ctx,
            entriesArray.AsSpan(job.StartIdx, job.Count),
            bufferArray.AsSpan(job.StartIdx, job.Count),
            ref childPath,
            job.CurrentChild,
            state.FlipCount,
            state.Flags);
    }

    [SkipLocalsInit]
    private TrieNode? BulkSetOne(in BulkSetEntry entry, ref TreePath path, TrieNode? node)
    {
        Span<byte> nibble = stackalloc byte[64];
        Nibbles.BytesToNibbleBytes(entry.Path.BytesAsSpan, nibble);
        Span<byte> remainingKey = nibble[path.Length..];

        TraverseStack traverseStack = GetTraverseStack();
        TrieNode? result = SetNew(traverseStack, remainingKey, entry.Value, ref path, node);
        ReturnTraverseStack(traverseStack);
        return result;
    }

    private TrieNode MakeFakeBranch(ref TreePath currentPath, TrieNode existingNode)
    {
        byte[] existingKey = existingNode.Key!;
        ReadOnlySpan<byte> shortenedKey = existingKey.AsSpan(1, existingKey.Length - 1);

        int branchIdx = existingKey[0];

        TrieNode newChild;

        if (existingNode.IsLeaf)
        {
            newChild = TrieNodeFactory.CreateLeaf(shortenedKey, existingNode.Value);
        }
        else
        {
            TrieNode child = existingNode.GetChild(TrieStore, ref currentPath, 0)
                ?? throw new InvalidOperationException("An extension node cannot be converted to a branch without a child.");

            if (existingKey.Length == 1)
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
    /// Branchless sort for 2-3 entries using compare-and-swap.
    /// Same contract as <see cref="BucketSort16"/>: returns usedMask and populates indexes.
    /// </summary>
    internal static int SortTiny(
        Span<BulkSetEntry> entries,
        int pathIndex,
        Span<int> indexes)
    {
        byte n0 = entries[0].GetPathNibble(pathIndex);
        byte n1 = entries[1].GetPathNibble(pathIndex);

        if (entries.Length == 2)
        {
            if (n0 > n1)
            {
                (entries[0], entries[1]) = (entries[1], entries[0]);
                (n0, n1) = (n1, n0);
            }
            indexes[n0] = 0;
            int nibMask = (1 << n0) | (1 << n1);
            if (n0 != n1) indexes[n1] = 1;
            return nibMask;
        }

        // Length == 3: sorting network, 3 compare-and-swaps
        byte n2 = entries[2].GetPathNibble(pathIndex);
        if (n0 > n1) { (entries[0], entries[1]) = (entries[1], entries[0]); (n0, n1) = (n1, n0); }
        if (n1 > n2) { (entries[1], entries[2]) = (entries[2], entries[1]); (n1, n2) = (n2, n1); }
        if (n0 > n1) { (entries[0], entries[1]) = (entries[1], entries[0]); (n0, n1) = (n1, n0); }

        indexes[n0] = 0;
        int mask = (1 << n0) | (1 << n1) | (1 << n2);
        if (n0 != n1) indexes[n1] = 1;
        if (n1 != n2) indexes[n2] = 2;
        return mask;
    }

    /// <summary>
    /// In-place variant of <see cref="BucketSort16"/> using index-sort + permute.
    /// Sorts small (index, nibble) pairs (8 bytes) instead of full BulkSetEntry (40 bytes),
    /// then permutes entries in-place via cycle-following.
    /// Same contract: returns usedMask and populates indexes.
    /// </summary>
    internal static int InPlaceBucketSort16(
        Span<BulkSetEntry> entries,
        int pathIndex,
        Span<int> indexes)
    {
        Span<(int idx, byte nib)> sorted = stackalloc (int, byte)[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            sorted[i] = (i, entries[i].GetPathNibble(pathIndex));

        for (int i = 1; i < sorted.Length; i++)
        {
            (int idx, byte nib) key = sorted[i];
            int j = i - 1;
            while (j >= 0 && sorted[j].nib > key.nib)
            {
                sorted[j + 1] = sorted[j];
                j--;
            }
            sorted[j + 1] = key;
        }

        int usedMask = 0;
        byte prevNib = byte.MaxValue;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].nib != prevNib)
            {
                indexes[sorted[i].nib] = i;
                usedMask |= 1 << sorted[i].nib;
                prevNib = sorted[i].nib;
            }
        }

        // Cycle-following permutation to rearrange entries in-place
        for (int i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].idx == i) continue;

            BulkSetEntry temp = entries[i];
            int j = i;
            do
            {
                int src = sorted[j].idx;
                sorted[j].idx = j;
                if (src == i)
                {
                    entries[j] = temp;
                    break;
                }
                entries[j] = entries[src];
                j = src;
            } while (true);
        }

        return usedMask;
    }

    /// <summary>
    /// Partially sort the <see cref="entries"/> based on the nibble at <see cref="pathIndex"/>> while at the same time
    /// populate <see cref="indexes"/> similar to <see cref="HexarySearchAlreadySorted"/>. Output is set to <see cref="sortTarget"/>.
    /// </summary>
    /// <param name="entries"></param>
    /// <param name="sortTarget"></param>
    /// <param name="nibbleBuffer"></param>
    /// <param name="pathIndex"></param>
    /// <param name="indexes"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    internal static int BucketSort16(
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> sortTarget,
        int pathIndex,
        Span<int> indexes)
    {

#if DEBUG
        if (entries.Length != sortTarget.Length)
            throw new Exception("Both buffer must be of the same length");
#endif

        if (entries.Length < 24)
            return BucketSort16Small(entries, sortTarget, pathIndex, indexes);

        return BucketSort16Large(entries, sortTarget, pathIndex, indexes);
    }

    internal static int BucketSort16Large(
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> sortTarget,
        int pathIndex,
        Span<int> indexes)
    {
        // You know, I originally used another buffer to keep track of the entries per nibble. then ChatGPT gave me this.
        // I don't know what is worse, that ChatGPT beat me to it, or that it is simpler.

        Span<int> counts = stackalloc int[TrieNode.BranchesCount];

        for (int i = 0; i < entries.Length; i++)
        {
            byte nib = entries[i].GetPathNibble(pathIndex);
            counts[nib]++;
        }

        int usedMask = 0;
        Span<int> starts = stackalloc int[TrieNode.BranchesCount];
        int total = 0;

        for (int nib = 0; nib < TrieNode.BranchesCount; nib++)
        {
            starts[nib] = total;
            total += counts[nib];

            if (counts[nib] != 0)
            {
                usedMask |= 1 << nib;
                indexes[nib] = starts[nib];
            }
        }

        for (int i = 0; i < entries.Length; i++)
        {
            int nib = entries[i].GetPathNibble(pathIndex);
            sortTarget[starts[nib]++] = entries[i];
        }

        return usedMask;
    }

    internal static int BucketSort16Small(
        Span<BulkSetEntry> entries,
        Span<BulkSetEntry> sortTarget,
        int pathIndex,
        Span<int> indexes)
    {
        // The small variant keeps track of used nibbles to skip looping unused nibble.
        int usedMask = 0;

        Span<int> counts = stackalloc int[TrieNode.BranchesCount];

        for (int i = 0; i < entries.Length; i++)
        {
            byte nib = entries[i].GetPathNibble(pathIndex);
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
            indexes[nib] = starts[nib];

            mask &= mask - 1; // clear lowest 1-bit
        }

        for (int i = 0; i < entries.Length; i++)
        {
            int nib = entries[i].GetPathNibble(pathIndex);
            sortTarget[starts[nib]++] = entries[i];
        }

        return usedMask;
    }

    /// <summary>
    /// Populate the <see cref="indexes"/> to the starting index of each nibble. It skip nibble that is missing and
    /// returns the number of unique nibble. It assume <see cref="entries"/> is already sorted.
    /// </summary>
    /// <param name="entries"></param>
    /// <param name="pathIndex"></param>
    /// <param name="indexes"></param>
    /// <returns></returns>
    internal static int HexarySearchAlreadySorted(Span<BulkSetEntry> entries, int pathIndex, Span<int> indexes) =>
        entries.Length < BSearchThreshold
            ? HexarySearchAlreadySortedSmall(entries, pathIndex, indexes)
            : HexarySearchAlreadySortedLarge(entries, pathIndex, indexes);

    internal static int HexarySearchAlreadySortedSmall(Span<BulkSetEntry> entries, int pathIndex, Span<int> indexes)
    {
        int curIdx = 0;
        int usedMask = 0;

        for (int i = 0; i < entries.Length && curIdx < TrieNode.BranchesCount; i++)
        {
            byte currentNib = entries[i].GetPathNibble(pathIndex);

            if (currentNib > curIdx)
            {
                curIdx = currentNib;
            }

            if (currentNib == curIdx)
            {
                indexes[currentNib] = i;
                usedMask |= 1 << currentNib;
                curIdx++;
            }
        }

        return usedMask;
    }

    [SkipLocalsInit]
    internal static int HexarySearchAlreadySortedLarge(
        Span<BulkSetEntry> entries,
        int pathIndex,
        Span<int> indexes)
    {
        int n = entries.Length;

        if (n == 0)
            return 0;

        // All hi
        Span<int> his = stackalloc int[TrieNode.BranchesCount];
        his.Fill(n);

        int nib = entries[0].GetPathNibble(pathIndex);

        // First nib is free
        int usedMask = 0;
        usedMask |= 1 << nib;
        indexes[nib] = 0;
        nib++;

        int lo = 0;

        for (; nib < TrieNode.BranchesCount;)
        {
            int hi = his[nib];

            while (lo < hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                int midnib = entries[mid].GetPathNibble(pathIndex);
                if (midnib < nib)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                    // Also fill all hi for nib between nib to midnib.
                    if (midnib > nib) his[(nib + 1)..midnib].Fill(mid);
                }
            }

            if (lo == n)
                break;

            // Note: The nib can be different, but its fine as it automatically skip.
            nib = entries[lo].GetPathNibble(pathIndex);
            usedMask |= 1 << nib;
            indexes[nib] = lo;

            nib++;
            lo++;
        }

        return usedMask;
    }

    private static int GetSpanOffset<T>(T[] array, Span<T> span)
    {
        ref T spanRef = ref MemoryMarshal.GetReference(span);
        ref T arrRef = ref MemoryMarshal.GetArrayDataReference(array);

        return (int)(Unsafe.ByteOffset(ref arrRef, ref spanRef) / Unsafe.SizeOf<T>());
    }
}
