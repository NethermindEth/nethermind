// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using MemoryMarshal = System.Runtime.InteropServices.MemoryMarshal;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Multi-core batch hasher: partitions the batch into contiguous per-worker slices and hashes each message with
/// <see cref="KeccakHash.ComputeHash"/> (already vectorized per message on AVX-512 hardware).
/// </summary>
/// <remarks>
/// The guaranteed low-risk production CPU backend. Work is split across physical cores via
/// <see cref="ParallelUnbalancedWork"/>; each worker writes only its own output slots, so there is no shared mutable
/// state and <see cref="HashBatch"/> is safe to call concurrently on one instance. Batches below the threshold hash on
/// the calling thread, avoiding scheduling overhead that would dominate small batches.
/// Both paths reject malformed offsets with <see cref="ArgumentException"/>: the inline path via span slicing, the
/// parallel path via an explicit up-front scan (the pointer-based worker has no slice bounds check to fall back on).
/// </remarks>
public sealed class ParallelKeccakBatchHasher : IKeccakBatchHasher
{
    /// <summary>Default minimum message count before work is spread across cores; below it the calling thread hashes inline.</summary>
    public const int DefaultParallelThreshold = 256;

    private readonly int _parallelThreshold;

    /// <summary>Creates a hasher that parallelizes batches of at least <see cref="DefaultParallelThreshold"/> messages.</summary>
    public ParallelKeccakBatchHasher() : this(DefaultParallelThreshold) { }

    /// <summary>Creates a hasher with an explicit parallelization threshold.</summary>
    /// <param name="parallelThreshold">Minimum message count before work is spread across cores; batches below it hash inline. Injectable for testing the parallel path on small batches.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parallelThreshold"/> is negative.</exception>
    public ParallelKeccakBatchHasher(int parallelThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parallelThreshold);
        _parallelThreshold = parallelThreshold;
    }

    /// <inheritdoc/>
    public unsafe void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        if (offsets.Length != outputs.Length) ThrowLengthMismatch();
        // O(1) trailing-bytes guard; non-monotonic offsets fail naturally via the range slice in HashRange.
        if (offsets.Length > 0 && offsets[^1] != flat.Length) ThrowLastOffsetMismatch();

        int count = offsets.Length;
        if (count < _parallelThreshold)
        {
            HashRange(flat, offsets, outputs, 0, count);
            return;
        }

        // The parallel worker reads flat via raw pointers, so malformed offsets would read past the pinned buffer
        // silently (memory unsafety, not an exception). Validate the full monotonic-and-in-bounds contract up front;
        // the O(n) int scan is negligible next to hashing. The inline path above gets this for free from span slicing.
        ValidateOffsets(offsets, flat.Length);

        int workers = Math.Min(count, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16.MaxDegreeOfParallelism);

        // Pin all three buffers so worker threads can address them via pointers across the (synchronous) parallel region.
        fixed (byte* flatPtr = flat)
        fixed (int* offsetsPtr = offsets)
        fixed (ValueHash256* outputsPtr = outputs)
        {
            BatchView view = new(flatPtr, offsetsPtr, outputsPtr, count, workers);
            ParallelUnbalancedWork.For(
                0,
                workers,
                RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                view,
                static (worker, v) =>
                {
                    // Contiguous, disjoint slice per worker: no two workers touch the same offset or output slot.
                    int start = (int)((long)worker * v.Count / v.Workers);
                    int end = (int)((long)(worker + 1) * v.Count / v.Workers);
                    for (int i = start; i < end; i++)
                    {
                        int inStart = i == 0 ? 0 : v.Offsets[i - 1];
                        int inEnd = v.Offsets[i];
                        ReadOnlySpan<byte> input = new(v.Flat + inStart, inEnd - inStart);
                        Span<byte> output = MemoryMarshal.AsBytes(new Span<ValueHash256>(v.Outputs + i, 1));
                        KeccakHash.ComputeHash(input, output);
                    }
                    return v;
                },
                static _ => { });
        }
    }

    // Inline per-message hashing over messages [from, to); used for the small-batch fast path.
    private static void HashRange(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs, int from, int to)
    {
        int start = from == 0 ? 0 : offsets[from - 1];
        for (int i = from; i < to; i++)
        {
            int end = offsets[i];
            ReadOnlySpan<byte> input = flat[start..end];
            Span<byte> output = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref outputs[i], 1));
            KeccakHash.ComputeHash(input, output);
            start = end;
        }
    }

    // Pointer view of the pinned batch buffers, capturable by the worker closure (spans cannot cross the closure boundary).
    private readonly unsafe struct BatchView(byte* flat, int* offsets, ValueHash256* outputs, int count, int workers)
    {
        public byte* Flat { get; } = flat;
        public int* Offsets { get; } = offsets;
        public ValueHash256* Outputs { get; } = outputs;
        public int Count { get; } = count;
        public int Workers { get; } = workers;
    }

    // Full contract check for the pointer path: 0 <= offsets[i-1] <= offsets[i] <= flatLength for every i.
    private static void ValidateOffsets(ReadOnlySpan<int> offsets, int flatLength)
    {
        int prev = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            int end = offsets[i];
            if (end < prev || end > flatLength) ThrowInvalidOffsets();
            prev = end;
        }
    }

    private static void ThrowInvalidOffsets() =>
        throw new ArgumentException("offsets must be non-decreasing and within the flat input bounds.");

    private static void ThrowLengthMismatch() =>
        throw new ArgumentException("offsets and outputs must have equal length.");

    private static void ThrowLastOffsetMismatch() =>
        throw new ArgumentException("Last offset must equal the flat input length.");
}
