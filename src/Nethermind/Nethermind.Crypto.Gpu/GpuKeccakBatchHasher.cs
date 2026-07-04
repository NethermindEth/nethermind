// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto.Gpu;

/// <summary>Batch keccak256 hasher that offloads the permutation to a GPU accelerator via ILGPU.</summary>
/// <remarks>
/// One GPU thread hashes one message. The context and accelerator are created once at construction (probing failure is
/// reported by <see cref="TryCreate"/> returning <c>false</c>, never a thrown exception); the round constants live in a
/// persistent device buffer, and the input/offset/output device buffers plus the pinned host staging buffers are grown
/// geometrically and reused across calls. Dispatch is serialized per instance (a single accelerator with shared device
/// buffers) and blocking (<see cref="Accelerator.Synchronize"/> then copy-back) - correct for the shadow validation
/// lane; transfer/compute overlap via dedicated streams is a possible follow-up, so quoted throughput must account for
/// the serialized host-device transfer, not just kernel time. Messages are sorted by sponge-block count before flatten
/// so equal-length work lands on adjacent threads, minimizing warp divergence in the per-thread block loop; outputs are
/// unpermuted back to the caller's order after copy-back.
/// </remarks>
public sealed class GpuKeccakBatchHasher : IKeccakBatchHasher, IDisposable
{
    private const int Rate = KeccakBatchGrouping.Rate; // 136-byte keccak256 sponge rate
    private const int StateLanes = 25;
    private const int OutputLanes = 4; // 256-bit digest = first 4 state lanes

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private readonly MemoryBuffer1D<ulong, Stride1D.Dense> _roundConstants;
    private readonly Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<ulong>, ArrayView<ulong>> _kernel;

    // Shared mutable dispatch state, guarded by _dispatchLock (per-instance, not global).
    private readonly object _dispatchLock = new();
    private MemoryBuffer1D<byte, Stride1D.Dense>? _deviceInput;
    private MemoryBuffer1D<int, Stride1D.Dense>? _deviceOffsets;
    private MemoryBuffer1D<ulong, Stride1D.Dense>? _deviceOutputs;
    private PageLockedArray1D<byte>? _stagingInput;
    private PageLockedArray1D<int>? _stagingOffsets;
    private PageLockedArray1D<ulong>? _stagingOutputs;

    private volatile bool _disposed;

    private GpuKeccakBatchHasher(Context context, Accelerator accelerator)
    {
        _context = context;
        _accelerator = accelerator;
        AcceleratorName = accelerator.Name;
        AcceleratorMemoryBytes = accelerator.MemorySize;
        _roundConstants = accelerator.Allocate1D(RoundConstants);
        _kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<ulong>, ArrayView<ulong>>(Keccak256Kernel);
    }

    /// <summary>Name of the selected accelerator device (e.g. the GPU model).</summary>
    public string AcceleratorName { get; }

    /// <summary>Total memory of the selected accelerator device, in bytes.</summary>
    public long AcceleratorMemoryBytes { get; }

    /// <summary>Attempts to create a GPU hasher on the best available non-CPU accelerator.</summary>
    /// <param name="hasher">The created hasher on success; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if a non-CPU accelerator was found and initialized; <c>false</c> on any probing failure.</returns>
    /// <remarks>
    /// Never throws: any exception during context/accelerator creation is treated as "no GPU available". CPU accelerators
    /// are skipped entirely; remaining devices are ranked Cuda &gt; OpenCL, then by descending device memory, so a discrete
    /// card is chosen over an integrated GPU deterministically. Devices are tried in rank order - if the top device fails
    /// to create an accelerator or compile the kernel, the next-ranked device is tried before giving up.
    /// </remarks>
    public static bool TryCreate(out GpuKeccakBatchHasher? hasher)
    {
        hasher = null;
        Context? context = null;
        try
        {
            context = Context.Create(builder => builder.Default().AllAccelerators());

            List<Device> ranked = [];
            foreach (Device device in context.Devices)
            {
                if (device.AcceleratorType != AcceleratorType.CPU) ranked.Add(device);
            }
            ranked.Sort(static (a, b) => DeviceScore(b).CompareTo(DeviceScore(a))); // descending

            foreach (Device device in ranked)
            {
                Accelerator? accelerator = null;
                try
                {
                    accelerator = device.CreateAccelerator(context);
                    hasher = new GpuKeccakBatchHasher(context, accelerator); // ownership of context + accelerator transfers here
                    return true;
                }
                catch
                {
                    accelerator?.Dispose(); // ctor throw points (buffer alloc / kernel compile) must not leak the accelerator
                }
            }

            context.Dispose();
            return false;
        }
        catch
        {
            context?.Dispose();
            hasher = null;
            return false;
        }
    }

    // Ranks non-CPU devices: Cuda outranks OpenCL, and within a type larger device memory wins. The memory field is
    // masked so it never overflows into the type-rank bits.
    private static long DeviceScore(Device device)
    {
        long typeRank = device.AcceleratorType switch
        {
            AcceleratorType.Cuda => 2,
            AcceleratorType.OpenCL => 1,
            _ => 0,
        };
        return (typeRank << 56) | (device.MemorySize & 0x00FF_FFFF_FFFF_FFFF);
    }

    /// <inheritdoc/>
    public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offsets.Length != outputs.Length) ThrowLengthMismatch();
        if (offsets.Length > 0 && offsets[^1] != flat.Length) ThrowLastOffsetMismatch();

        int count = offsets.Length;
        if (count == 0) return;

        // Full monotonic-and-in-bounds validation up front: the sorted flatten reads flat via computed spans, so a bad
        // offset must fail here as ArgumentException rather than corrupt the staging copy.
        ValidateOffsets(offsets, flat.Length);

        // Sort message indices by sponge-block count so equal-length work is contiguous on the GPU (warp divergence).
        int[] permutation = ArrayPool<int>.Shared.Rent(count);
        int[] groupBoundaries = ArrayPool<int>.Shared.Rent(KeccakBatchGrouping.MaxGroups(count));
        try
        {
            KeccakBatchGrouping.GroupByBlockCount(offsets, permutation, groupBoundaries);

            lock (_dispatchLock)
            {
                // Re-check under the lock: a concurrent Dispose (which also takes _dispatchLock) must not free the native
                // buffers mid-dispatch.
                ObjectDisposedException.ThrowIf(_disposed, this);
                Dispatch(flat, offsets, outputs, permutation.AsSpan(0, count));
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(permutation);
            ArrayPool<int>.Shared.Return(groupBoundaries);
        }
    }

    // Flattens in sorted order into pinned staging, uploads to persistent device buffers, launches, and unpermutes back.
    private void Dispatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs, ReadOnlySpan<int> permutation)
    {
        int count = offsets.Length;
        int totalBytes = flat.Length;

        EnsureCapacity(totalBytes, count);

        Span<byte> stagingInput = _stagingInput!.Span[..totalBytes];
        Span<int> stagingOffsets = _stagingOffsets!.Span[..count];

        // Copy each message into sorted position, recording running exclusive-end offsets in sorted space.
        int pos = 0;
        for (int sorted = 0; sorted < count; sorted++)
        {
            int original = permutation[sorted];
            int start = original == 0 ? 0 : offsets[original - 1];
            int end = offsets[original];
            int len = end - start;
            flat.Slice(start, len).CopyTo(stagingInput.Slice(pos, len));
            pos += len;
            stagingOffsets[sorted] = pos;
        }

        ArrayView<byte> inputView = _deviceInput!.View.SubView(0, totalBytes);
        ArrayView<int> offsetsView = _deviceOffsets!.View.SubView(0, count);
        ArrayView<ulong> outputsView = _deviceOutputs!.View.SubView(0, (long)count * OutputLanes);

        inputView.CopyFromCPU(ref MemoryMarshal.GetReference(stagingInput), totalBytes);
        offsetsView.CopyFromCPU(ref MemoryMarshal.GetReference(stagingOffsets), count);

        _kernel(count, offsetsView, inputView, _roundConstants.View, outputsView);
        _accelerator.Synchronize();

        int outputLaneCount = count * OutputLanes;
        Span<ulong> stagingOutputs = _stagingOutputs!.Span[..outputLaneCount];
        outputsView.CopyToCPU(ref MemoryMarshal.GetReference(stagingOutputs), outputLaneCount);

        // Unpermute: the digest at sorted index j belongs to the original message permutation[j].
        for (int sorted = 0; sorted < count; sorted++)
        {
            int original = permutation[sorted];
            ref ValueHash256 dest = ref outputs[original];
            Span<ulong> destLanes = MemoryMarshal.Cast<byte, ulong>(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref dest, 1)));
            stagingOutputs.Slice(sorted * OutputLanes, OutputLanes).CopyTo(destLanes);
        }
    }

    // Grows device and pinned-host buffers geometrically; they are never shrunk, so steady-state has no allocations.
    private void EnsureCapacity(int totalBytes, int count)
    {
        int neededInput = Math.Max(totalBytes, 1);
        if (_deviceInput is null || _deviceInput.Length < neededInput)
        {
            _deviceInput?.Dispose();
            _stagingInput?.Dispose();
            int cap = GrowTo(_deviceInput?.Length ?? 0, neededInput);
            _deviceInput = _accelerator.Allocate1D<byte>(cap);
            _stagingInput = _accelerator.AllocatePageLocked1D<byte>(cap);
        }

        if (_deviceOffsets is null || _deviceOffsets.Length < count)
        {
            _deviceOffsets?.Dispose();
            _deviceOutputs?.Dispose();
            _stagingOffsets?.Dispose();
            _stagingOutputs?.Dispose();
            int cap = GrowTo((int)(_deviceOffsets?.Length ?? 0), count);
            _deviceOffsets = _accelerator.Allocate1D<int>(cap);
            _deviceOutputs = _accelerator.Allocate1D<ulong>((long)cap * OutputLanes);
            _stagingOffsets = _accelerator.AllocatePageLocked1D<int>(cap);
            _stagingOutputs = _accelerator.AllocatePageLocked1D<ulong>((long)cap * OutputLanes);
        }
    }

    private static int GrowTo(long current, int needed)
    {
        long cap = Math.Max(current, 1);
        while (cap < needed) cap *= 2;
        if (cap > int.MaxValue) cap = int.MaxValue;
        return (int)cap;
    }

    /// <summary>Keccak256 kernel: one GPU thread absorbs and squeezes one message; digest is written as 4 output lanes.</summary>
    private static void Keccak256Kernel(
        Index1D index,
        ArrayView<int> offsets,
        ArrayView<byte> inputs,
        ArrayView<ulong> roundConstants,
        ArrayView<ulong> outputs)
    {
        int i = index;
        int start = i == 0 ? 0 : offsets[i - 1];
        int end = offsets[i];

        // Per-thread local-memory scratch (25 lanes) - ILGPU-native indexing, no managed allocation inside the kernel.
        ArrayView<ulong> state = LocalMemory.Allocate1D<ulong>(StateLanes);
        for (int l = 0; l < StateLanes; l++) state[l] = 0UL;

        int p = start;
        while (end - p >= Rate)
        {
            AbsorbBlock(state, inputs, p, Rate);
            Permute(state, roundConstants);
            p += Rate;
        }

        int rem = end - p;
        AbsorbBlock(state, inputs, p, rem);
        // Pad: 0x01 at the first free byte and 0x80 at byte 135; coincide to 0x81 when rem == 135.
        XorStateByte(state, rem, 0x01);
        XorStateByte(state, Rate - 1, 0x80);
        Permute(state, roundConstants);

        long outBase = (long)i * OutputLanes;
        for (int l = 0; l < OutputLanes; l++) outputs[outBase + l] = state[l];
    }

    private static void AbsorbBlock(ArrayView<ulong> state, ArrayView<byte> inputs, int blockStart, int blockLen)
    {
        for (int j = 0; j < blockLen; j++)
        {
            int lane = j >> 3;
            int shift = (j & 7) << 3;
            state[lane] ^= (ulong)inputs[blockStart + j] << shift;
        }
    }

    private static void XorStateByte(ArrayView<ulong> state, int byteIndex, byte value)
    {
        int lane = byteIndex >> 3;
        int shift = (byteIndex & 7) << 3;
        state[lane] ^= (ulong)value << shift;
    }

    private static void Permute(ArrayView<ulong> s, ArrayView<ulong> rc)
    {
        for (int round = 0; round < 24; round++)
        {
            // Theta
            ulong c0 = s[0] ^ s[5] ^ s[10] ^ s[15] ^ s[20];
            ulong c1 = s[1] ^ s[6] ^ s[11] ^ s[16] ^ s[21];
            ulong c2 = s[2] ^ s[7] ^ s[12] ^ s[17] ^ s[22];
            ulong c3 = s[3] ^ s[8] ^ s[13] ^ s[18] ^ s[23];
            ulong c4 = s[4] ^ s[9] ^ s[14] ^ s[19] ^ s[24];

            ulong d0 = c4 ^ Rotl(c1, 1);
            ulong d1 = c0 ^ Rotl(c2, 1);
            ulong d2 = c1 ^ Rotl(c3, 1);
            ulong d3 = c2 ^ Rotl(c4, 1);
            ulong d4 = c3 ^ Rotl(c0, 1);

            s[0] ^= d0; s[5] ^= d0; s[10] ^= d0; s[15] ^= d0; s[20] ^= d0;
            s[1] ^= d1; s[6] ^= d1; s[11] ^= d1; s[16] ^= d1; s[21] ^= d1;
            s[2] ^= d2; s[7] ^= d2; s[12] ^= d2; s[17] ^= d2; s[22] ^= d2;
            s[3] ^= d3; s[8] ^= d3; s[13] ^= d3; s[18] ^= d3; s[23] ^= d3;
            s[4] ^= d4; s[9] ^= d4; s[14] ^= d4; s[19] ^= d4; s[24] ^= d4;

            // Rho + Pi
            ulong b0 = s[0];
            ulong b1 = Rotl(s[6], 44);
            ulong b2 = Rotl(s[12], 43);
            ulong b3 = Rotl(s[18], 21);
            ulong b4 = Rotl(s[24], 14);
            ulong b5 = Rotl(s[3], 28);
            ulong b6 = Rotl(s[9], 20);
            ulong b7 = Rotl(s[10], 3);
            ulong b8 = Rotl(s[16], 45);
            ulong b9 = Rotl(s[22], 61);
            ulong b10 = Rotl(s[1], 1);
            ulong b11 = Rotl(s[7], 6);
            ulong b12 = Rotl(s[13], 25);
            ulong b13 = Rotl(s[19], 8);
            ulong b14 = Rotl(s[20], 18);
            ulong b15 = Rotl(s[4], 27);
            ulong b16 = Rotl(s[5], 36);
            ulong b17 = Rotl(s[11], 10);
            ulong b18 = Rotl(s[17], 15);
            ulong b19 = Rotl(s[23], 56);
            ulong b20 = Rotl(s[2], 62);
            ulong b21 = Rotl(s[8], 55);
            ulong b22 = Rotl(s[14], 39);
            ulong b23 = Rotl(s[15], 41);
            ulong b24 = Rotl(s[21], 2);

            // Chi
            s[0] = b0 ^ (~b1 & b2);
            s[1] = b1 ^ (~b2 & b3);
            s[2] = b2 ^ (~b3 & b4);
            s[3] = b3 ^ (~b4 & b0);
            s[4] = b4 ^ (~b0 & b1);
            s[5] = b5 ^ (~b6 & b7);
            s[6] = b6 ^ (~b7 & b8);
            s[7] = b7 ^ (~b8 & b9);
            s[8] = b8 ^ (~b9 & b5);
            s[9] = b9 ^ (~b5 & b6);
            s[10] = b10 ^ (~b11 & b12);
            s[11] = b11 ^ (~b12 & b13);
            s[12] = b12 ^ (~b13 & b14);
            s[13] = b13 ^ (~b14 & b10);
            s[14] = b14 ^ (~b10 & b11);
            s[15] = b15 ^ (~b16 & b17);
            s[16] = b16 ^ (~b17 & b18);
            s[17] = b17 ^ (~b18 & b19);
            s[18] = b18 ^ (~b19 & b15);
            s[19] = b19 ^ (~b15 & b16);
            s[20] = b20 ^ (~b21 & b22);
            s[21] = b21 ^ (~b22 & b23);
            s[22] = b22 ^ (~b23 & b24);
            s[23] = b23 ^ (~b24 & b20);
            s[24] = b24 ^ (~b20 & b21);

            // Iota
            s[0] ^= rc[round];
        }
    }

    private static ulong Rotl(ulong value, int offset) => (value << offset) | (value >> (64 - offset));

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

    /// <inheritdoc/>
    /// <remarks>Takes the dispatch lock so an in-flight <see cref="HashBatch"/> completes before native buffers are freed.</remarks>
    public void Dispose()
    {
        lock (_dispatchLock)
        {
            if (_disposed) return;
            _disposed = true;

            _deviceInput?.Dispose();
            _deviceOffsets?.Dispose();
            _deviceOutputs?.Dispose();
            _stagingInput?.Dispose();
            _stagingOffsets?.Dispose();
            _stagingOutputs?.Dispose();
            _roundConstants.Dispose();
            _accelerator.Dispose();
            _context.Dispose();
        }
    }
}
