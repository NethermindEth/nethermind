// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading.Tasks;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using MemoryMarshal = System.Runtime.InteropServices.MemoryMarshal;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// BENCHMARK-LOCAL experiment: composes the vertical 8-way SIMD kernel
/// (<see cref="MultiBufferKeccakBatchHasher"/>, single-core 1.3-1.55x over per-message) with multi-core
/// work-stealing (the shape of <see cref="ParallelKeccakBatchHasher"/>, 7.5-11.7x). Measures whether the two
/// speedups multiply. Not production code; lives only in the benchmark project.
/// </summary>
/// <remarks>
/// Grouping is done ONCE up front on the whole batch via the public <see cref="KeccakBatchGrouping.GroupByBlockCount"/>,
/// producing a block-count-ascending permutation and uniform-group boundaries. Work items are then formed: every full
/// 8-wide slice of a uniform group is one work item; the &lt;8 remainder of each group is one per-message work item.
/// Work-item indices are distributed across physical cores with <see cref="ParallelUnbalancedWork"/> per-index stealing,
/// matching the work-stealing hasher's rebalancing shape (so a few long runs cannot pin one worker).
///
/// RE-ENTRY OVERHEAD NOTE: <see cref="MultiBufferKeccakBatchHasher"/> exposes only <see cref="IKeccakBatchHasher.HashBatch"/>;
/// its kernel entry <c>HashEightUniform</c> is private. So each 8-run work item is hashed by calling
/// <see cref="MultiBufferKeccakBatchHasher.HashBatch"/> on a per-run sub-batch: the worker gathers its 8 permuted
/// messages into a thread-local contiguous flat buffer + 8-element offsets, then invokes HashBatch on that slice
/// (which trivially re-groups the 8 uniform messages into one run and dispatches the same vertical kernel). The extra
/// cost is: the per-run gather copy of &lt;=8*Rate*ceil bytes, plus HashBatch's own O(8) offsets validation and
/// counting-sort scratch (stackalloc, no heap). This distorts the reading slightly against the (unreachable) direct
/// kernel path; the effect is reported honestly in the results. Remainder messages fall back per-message via
/// <see cref="KeccakHash.ComputeHash"/>, exactly as the kernel's own remainder path does.
/// </remarks>
public sealed class ParallelMultiBufferKeccakBatchHasher : IKeccakBatchHasher
{
    private const int Width = 8;

    private readonly ParallelOptions _options;
    private readonly MultiBufferKeccakBatchHasher _kernel = new(MultiBufferGroupingStrategy.UniformGroups);

    /// <summary>Full-DOP variant: uses the same core budget as the work-stealing hasher (min(logical, 16)).</summary>
    public ParallelMultiBufferKeccakBatchHasher()
        : this(RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16.MaxDegreeOfParallelism) { }

    /// <summary>Explicit degree-of-parallelism variant (for the DOP-4 frugality reading).</summary>
    public ParallelMultiBufferKeccakBatchHasher(int maxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);
        _options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
    }

    /// <inheritdoc/>
    public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        if (offsets.Length != outputs.Length) throw new ArgumentException("offsets and outputs must have equal length.");
        if (offsets.Length > 0 && offsets[^1] != flat.Length) throw new ArgumentException("Last offset must equal the flat input length.");

        int count = offsets.Length;
        if (count == 0) return;

        // Reuse the shared validation contract (same up-front scan as the kernel / work-stealing pointer path).
        ValidateOffsets(offsets, flat.Length);

        // If the ISA cannot run the vertical kernel there is nothing to compose; fall back to per-message so the
        // backend is always correct (mirrors the kernel's own IsSupported gate).
        if (!MultiBufferKeccakBatchHasher.IsSupported)
        {
            HashRangePerMessage(flat, offsets, outputs, 0, count);
            return;
        }

        // ---- Grouping (once, on the calling thread) ----
        int[] permutation = new int[count];
        int[] groupBoundaries = new int[count];
        int groupCount = KeccakBatchGrouping.GroupByBlockCount(offsets, permutation, groupBoundaries);

        // Build the flat list of work items across all groups: each full 8-run and each group's remainder.
        // WorkItem encodes [permStart, permLen, isFullRun]; a full run has permLen == 8, a remainder permLen in 1..7.
        WorkItem[] workItems = BuildWorkItems(groupBoundaries, groupCount);
        int workItemCount = workItems.Length;

        if (workItemCount == 0) return;

        // Small batches: no scheduling, run inline (matches the work-stealing hasher's below-threshold behavior in spirit).
        if (workItemCount == 1 || count < ParallelKeccakBatchHasher.DefaultParallelThreshold)
        {
            for (int w = 0; w < workItemCount; w++)
            {
                RunWorkItem(workItems[w], flat, offsets, permutation, outputs, _kernel);
            }
            return;
        }

        // ---- Distribute work items across cores with per-index stealing (the work-stealing hasher's shape). ----
        // Safety: offsets were fully validated (monotonic, in-bounds, last == flat.Length) before pinning;
        // the fixed block encloses the synchronous For (a hard barrier - no worker outlives the pins);
        // flat/offsets/permutation/workItems are read-only in workers; each work item owns a disjoint
        // range of output indices, so no two workers write the same slot.
        unsafe
        {
            fixed (byte* flatPtr = flat)
            fixed (int* offsetsPtr = offsets)
            fixed (int* permPtr = permutation)
            fixed (ValueHash256* outPtr = outputs)
            fixed (WorkItem* wiPtr = workItems)
            {
                View view = new(flatPtr, offsetsPtr, permPtr, outPtr, wiPtr, _kernel);
                ParallelUnbalancedWork.For(
                    0,
                    workItemCount,
                    _options,
                    view,
                    static (w, v) =>
                    {
                        RunWorkItemUnsafe(v.WorkItems[w], v, v.Kernel);
                        return v;
                    },
                    static _ => { });
            }
        }
    }

    private readonly record struct WorkItem(int PermStart, int PermLen, bool IsFullRun);

    // One work item per full 8-run, one per group remainder. Ordering is irrelevant (per-index stealing handles balance).
    private static WorkItem[] BuildWorkItems(ReadOnlySpan<int> groupBoundaries, int groupCount)
    {
        // Count first so we can size exactly (no List growth in the measured setup path).
        int items = 0;
        int gStart = 0;
        for (int g = 0; g < groupCount; g++)
        {
            int gEnd = groupBoundaries[g];
            int size = gEnd - gStart;
            items += size / Width;             // full 8-runs
            if (size % Width != 0) items++;     // one remainder item
            gStart = gEnd;
        }

        WorkItem[] result = new WorkItem[items];
        int idx = 0;
        gStart = 0;
        for (int g = 0; g < groupCount; g++)
        {
            int gEnd = groupBoundaries[g];
            int size = gEnd - gStart;
            int full = gStart + (size / Width) * Width;
            for (int b = gStart; b < full; b += Width)
            {
                result[idx++] = new WorkItem(b, Width, IsFullRun: true);
            }
            if (full < gEnd)
            {
                result[idx++] = new WorkItem(full, gEnd - full, IsFullRun: false);
            }
            gStart = gEnd;
        }
        return result;
    }

    // Managed path used by the inline (small-batch) branch.
    private static void RunWorkItem(
        WorkItem wi, ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, ReadOnlySpan<int> permutation,
        Span<ValueHash256> outputs, MultiBufferKeccakBatchHasher kernel)
    {
        if (!wi.IsFullRun)
        {
            for (int p = wi.PermStart; p < wi.PermStart + wi.PermLen; p++)
            {
                int m = permutation[p];
                int start = m == 0 ? 0 : offsets[m - 1];
                HashOne(flat.Slice(start, offsets[m] - start), ref outputs[m]);
            }
            return;
        }

        // Gather the 8 permuted messages into a contiguous sub-batch and invoke the vertical kernel via HashBatch.
        Span<int> idxSpan = stackalloc int[Width];
        int subTotal = 0;
        for (int j = 0; j < Width; j++)
        {
            int m = permutation[wi.PermStart + j];
            idxSpan[j] = m;
            int mstart = m == 0 ? 0 : offsets[m - 1];
            subTotal += offsets[m] - mstart;
        }

        byte[] subFlat = new byte[subTotal];
        int[] subOffsets = new int[Width];
        int pos = 0;
        for (int j = 0; j < Width; j++)
        {
            int m = idxSpan[j];
            int mstart = m == 0 ? 0 : offsets[m - 1];
            int len = offsets[m] - mstart;
            flat.Slice(mstart, len).CopyTo(subFlat.AsSpan(pos));
            pos += len;
            subOffsets[j] = pos;
        }

        Span<ValueHash256> subOut = stackalloc ValueHash256[Width];
        kernel.HashBatch(subFlat, subOffsets, subOut);
        for (int j = 0; j < Width; j++) outputs[idxSpan[j]] = subOut[j];
    }

    // Pointer path used inside the parallel region (spans cannot cross the worker closure).
    private static unsafe void RunWorkItemUnsafe(WorkItem wi, View v, MultiBufferKeccakBatchHasher kernel)
    {
        if (!wi.IsFullRun)
        {
            for (int p = wi.PermStart; p < wi.PermStart + wi.PermLen; p++)
            {
                int m = v.Perm[p];
                int start = m == 0 ? 0 : v.Offsets[m - 1];
                int len = v.Offsets[m] - start;
                ReadOnlySpan<byte> input = new(v.Flat + start, len);
                Span<byte> output = MemoryMarshal.AsBytes(new Span<ValueHash256>(v.Outputs + m, 1));
                KeccakHash.ComputeHash(input, output);
            }
            return;
        }

        Span<int> idxSpan = stackalloc int[Width];
        int subTotal = 0;
        for (int j = 0; j < Width; j++)
        {
            int m = v.Perm[wi.PermStart + j];
            idxSpan[j] = m;
            int mstart = m == 0 ? 0 : v.Offsets[m - 1];
            subTotal += v.Offsets[m] - mstart;
        }

        // Thread-local gather buffer for this run's 8 messages. Rented per work item; the copy is the honest re-entry
        // overhead noted in the type remarks.
        byte[] subFlat = new byte[subTotal];
        Span<int> subOffsets = stackalloc int[Width];
        int pos = 0;
        for (int j = 0; j < Width; j++)
        {
            int m = idxSpan[j];
            int mstart = m == 0 ? 0 : v.Offsets[m - 1];
            int len = v.Offsets[m] - mstart;
            new ReadOnlySpan<byte>(v.Flat + mstart, len).CopyTo(subFlat.AsSpan(pos));
            pos += len;
            subOffsets[j] = pos;
        }

        Span<ValueHash256> subOut = stackalloc ValueHash256[Width];
        kernel.HashBatch(subFlat, subOffsets, subOut);
        for (int j = 0; j < Width; j++)
        {
            *(v.Outputs + idxSpan[j]) = subOut[j];
        }
    }

    private readonly unsafe struct View(
        byte* flat, int* offsets, int* perm, ValueHash256* outputs, WorkItem* workItems, MultiBufferKeccakBatchHasher kernel)
    {
        public byte* Flat { get; } = flat;
        public int* Offsets { get; } = offsets;
        public int* Perm { get; } = perm;
        public ValueHash256* Outputs { get; } = outputs;
        public WorkItem* WorkItems { get; } = workItems;
        public MultiBufferKeccakBatchHasher Kernel { get; } = kernel;
    }

    private static void HashRangePerMessage(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs, int from, int to)
    {
        int start = from == 0 ? 0 : offsets[from - 1];
        for (int i = from; i < to; i++)
        {
            int end = offsets[i];
            HashOne(flat[start..end], ref outputs[i]);
            start = end;
        }
    }

    private static void HashOne(ReadOnlySpan<byte> input, ref ValueHash256 output) =>
        KeccakHash.ComputeHash(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref output, 1)));

    private static void ValidateOffsets(ReadOnlySpan<int> offsets, int flatLength)
    {
        int prev = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            int end = offsets[i];
            if (end < prev || end > flatLength) throw new ArgumentException("offsets must be non-decreasing and within the flat input bounds.");
            prev = end;
        }
    }
}
