// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Crypto;

/// <summary>Selects how the vertical kernel forms 8-wide lane groups from a batch of mixed-length messages.</summary>
public enum MultiBufferGroupingStrategy
{
    /// <summary>Lane groups contain only messages with the same block count; the &lt;8 remainder of each count hashes per-message.</summary>
    UniformGroups,

    /// <summary>Lane groups mix block counts and run to the group maximum, snapshotting each message's digest at its own final block.</summary>
    RunToMaxSnapshots,
}

/// <summary>
/// Experimental vertical multi-buffer keccak256 backend: hashes 8 messages at once, with element <c>j</c> of every
/// <see cref="Vector512{T}"/> holding one lane of message <c>j</c>'s sponge state.
/// </summary>
/// <remarks>
/// The vertical layout replaces every scalar keccak-f[1600] ulong operation with the corresponding 8-way vector
/// operation and uses no cross-element shuffles, so element <c>j</c> is byte-identical to hashing message <c>j</c>
/// alone. The 25-lane state (25 x <see cref="Vector512{T}"/> = 1600 bytes) is held in a stack scratch and streamed
/// five lanes at a time through registers per round, since 25 live vectors exceed the 32 zmm registers. Absorbing past
/// a message's final block would change its digest, so mixed-block-count groups either split by block count
/// (<see cref="MultiBufferGroupingStrategy.UniformGroups"/>) or snapshot each message at its own last block and stop
/// absorbing into it (<see cref="MultiBufferGroupingStrategy.RunToMaxSnapshots"/>).
/// Instances hold no mutable state, so <see cref="HashBatch"/> is safe to call concurrently.
/// </remarks>
/// <param name="strategy">How the batch is split into 8-wide lane groups; see <see cref="MultiBufferGroupingStrategy"/>.</param>
public sealed class MultiBufferKeccakBatchHasher(MultiBufferGroupingStrategy strategy = MultiBufferGroupingStrategy.UniformGroups) : IKeccakBatchHasher
{
    private const int Rate = KeccakBatchGrouping.Rate; // 136 bytes absorbed per sponge block
    private const int RateLanes = Rate / sizeof(ulong); // 17 ulong lanes per block
    private const int StateLanes = 25;
    private const int Rounds = 24;
    private const int Width = 8; // Vector512<ulong>.Count: 8 messages per vertical pass

    // keccak-f[1600] round constants; copied from KeccakHash.RoundConstants (private there).
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
        0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
        0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
        0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
        0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    ];

    private readonly MultiBufferGroupingStrategy _strategy = strategy;

    /// <summary>Whether the vertical AVX-512 kernel can run on this host; when false the fallback per-message path is used.</summary>
    public static bool IsSupported => Vector512.IsHardwareAccelerated && Avx512F.IsSupported;

    /// <inheritdoc/>
    public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        if (offsets.Length != outputs.Length) ThrowLengthMismatch();
        if (offsets.Length > 0 && offsets[^1] != flat.Length) ThrowLastOffsetMismatch();

        int count = offsets.Length;
        if (count == 0) return;

        // The kernel gathers input bytes via raw indexing; validate the whole monotonic-and-in-bounds contract up front
        // (the O(n) int scan is negligible next to hashing), mirroring ParallelKeccakBatchHasher's pointer-path guard.
        ValidateOffsets(offsets, flat.Length);

        // No accelerated Vector512 -> fall back so the backend is always correct, just unvectorized off AVX-512.
        if (!IsSupported)
        {
            HashPerMessage(flat, offsets, outputs, ReadOnlySpan<int>.Empty, 0, contiguous: true, to: count);
            return;
        }

        Span<int> permutation = count <= StackThreshold ? stackalloc int[count] : new int[count];
        Span<int> groupBoundaries = count <= StackThreshold ? stackalloc int[count] : new int[count];
        int groupCount = KeccakBatchGrouping.GroupByBlockCount(offsets, permutation, groupBoundaries);

        if (_strategy == MultiBufferGroupingStrategy.UniformGroups)
        {
            HashUniformGroups(flat, offsets, outputs, permutation, groupBoundaries, groupCount);
        }
        else
        {
            // The permutation alone (ascending by block count) drives run-to-max; group boundaries are only needed to
            // split uniform groups, which this strategy deliberately does not do.
            HashRunToMax(flat, offsets, outputs, permutation);
        }
    }

    // Strategy 1: within each uniform-block-count group, hash full 8-wide slices with the kernel; the <8 tail per-message.
    private static void HashUniformGroups(
        ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs,
        ReadOnlySpan<int> permutation, ReadOnlySpan<int> groupBoundaries, int groupCount)
    {
        int groupStart = 0;
        for (int g = 0; g < groupCount; g++)
        {
            int groupEnd = groupBoundaries[g];
            int groupSize = groupEnd - groupStart;
            int blockCount = BlockCountOf(offsets, permutation[groupStart]);

            int full = groupStart + (groupSize / Width) * Width;
            for (int b = groupStart; b < full; b += Width)
            {
                HashEightUniform(flat, offsets, outputs, permutation.Slice(b, Width), blockCount);
            }
            // Remainder of this uniform group (<8 messages): fall back to the per-message backend.
            HashPerMessage(flat, offsets, outputs, permutation, full, contiguous: false, to: groupEnd);

            groupStart = groupEnd;
        }
    }

    // Strategy 2: pack 8 messages of possibly-differing block counts, run to the group max, snapshot at each own final block.
    private static void HashRunToMax(
        ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs, ReadOnlySpan<int> permutation)
    {
        // The permutation is ascending by block count; slicing it into contiguous 8-runs keeps each run's max close to its
        // min (bounded waste) while still allowing mixed counts within a run at the group seams. A tail run of <8 falls back.
        int n = permutation.Length;
        int full = (n / Width) * Width;
        for (int b = 0; b < full; b += Width)
        {
            HashEightRunToMax(flat, offsets, outputs, permutation.Slice(b, Width));
        }
        HashPerMessage(flat, offsets, outputs, permutation, full, contiguous: false, to: n);
    }

    // Vertical hash of exactly 8 uniform-block-count messages. Every lane absorbs the same number of blocks.
    [SkipLocalsInit]
    private static void HashEightUniform(
        ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs, ReadOnlySpan<int> idx, int blockCount)
    {
        Span<Vector512<ulong>> state = stackalloc Vector512<ulong>[StateLanes];
        state.Clear();

        Span<int> starts = stackalloc int[Width];
        Span<int> lengths = stackalloc int[Width];
        for (int j = 0; j < Width; j++)
        {
            int m = idx[j];
            int start = m == 0 ? 0 : offsets[m - 1];
            starts[j] = start;
            lengths[j] = offsets[m] - start;
        }

        for (int block = 0; block < blockCount; block++)
        {
            AbsorbBlock(state, flat, starts, lengths, block, allLanes: true, activeMask: 0xFF);
            KeccakF(state);
        }

        WriteDigests(state, outputs, idx, activeMask: 0xFF);
    }

    // Vertical hash of 8 messages with mixed block counts: run to the max, snapshot each lane at its own last block.
    [SkipLocalsInit]
    private static void HashEightRunToMax(
        ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs, ReadOnlySpan<int> idx)
    {
        Span<Vector512<ulong>> state = stackalloc Vector512<ulong>[StateLanes];
        state.Clear();

        Span<int> starts = stackalloc int[Width];
        Span<int> lengths = stackalloc int[Width];
        Span<int> blockCounts = stackalloc int[Width];
        int maxBlocks = 0;
        for (int j = 0; j < Width; j++)
        {
            int m = idx[j];
            int start = m == 0 ? 0 : offsets[m - 1];
            starts[j] = start;
            int len = offsets[m] - start;
            lengths[j] = len;
            int bc = KeccakBatchGrouping.BlockCount(len);
            blockCounts[j] = bc;
            if (bc > maxBlocks) maxBlocks = bc;
        }

        // A lane is active for block `block` while block < its blockCount; once done it absorbs nothing (never zeros/pads
        // again) and its state is snapshotted, then left to become never-read garbage.
        int activeMask = (1 << Width) - 1;
        for (int block = 0; block < maxBlocks; block++)
        {
            AbsorbBlock(state, flat, starts, lengths, block, allLanes: false, activeMask: activeMask);
            KeccakF(state);

            // Snapshot every lane whose final (1-based) block is this one: digest is the post-permute state, per keccak.
            int finishing = 0;
            for (int j = 0; j < Width; j++)
            {
                if (blockCounts[j] == block + 1) finishing |= 1 << j;
            }
            if (finishing != 0)
            {
                WriteDigests(state, outputs, idx, activeMask: finishing);
                activeMask &= ~finishing;
            }
        }
    }

    // XORs the `block`-th 136-byte (padded) block of each active lane into the state. Lane j's message bytes are read
    // from flat[starts[j]..]; the standard 0x01/0x80 padding (coinciding to 0x81 when len % 136 == 135) is applied to
    // the lane's final block. `allLanes` skips the per-lane active test when every lane is known active (uniform groups).
    private static void AbsorbBlock(
        Span<Vector512<ulong>> state, ReadOnlySpan<byte> flat, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths,
        int block, bool allLanes, int activeMask)
    {
        // Materialize each active lane's padded 136-byte block once, so the transpose below reads straight ulongs.
        Span<byte> blocks = stackalloc byte[Width * Rate];
        int blockOffset = block * Rate;
        for (int j = 0; j < Width; j++)
        {
            if (!allLanes && (activeMask & (1 << j)) == 0) continue;

            Span<byte> laneBlock = blocks.Slice(j * Rate, Rate);
            int remaining = lengths[j] - blockOffset;
            if (remaining >= Rate)
            {
                flat.Slice(starts[j] + blockOffset, Rate).CopyTo(laneBlock);
            }
            else
            {
                // Final (partial) block for this lane: message tail then the 0x01/0x80 padding.
                laneBlock.Clear();
                int bodyLen = Math.Max(remaining, 0);
                if (bodyLen > 0)
                {
                    flat.Slice(starts[j] + blockOffset, bodyLen).CopyTo(laneBlock);
                }
                laneBlock[bodyLen] ^= 0x01;   // pad start (coincides with 0x80 to 0x81 when bodyLen == 135)
                laneBlock[Rate - 1] ^= 0x80;  // pad end
            }
        }

        Span<ulong> column = stackalloc ulong[Width];
        for (int l = 0; l < RateLanes; l++)
        {
            column.Clear();
            for (int j = 0; j < Width; j++)
            {
                if (!allLanes && (activeMask & (1 << j)) == 0) continue;
                column[j] = ReadUlong(blocks, j * Rate + l * sizeof(ulong));
            }
            state[l] = Vector512.Xor(state[l], Vector512.Create<ulong>(column));
        }
    }

    // In-place vertical keccak-f[1600]: the reference scalar round with every ulong op replaced by its Vector512 form.
    // Callers pass a 25-lane state scratch; a 25-lane B scratch is stack-owned here (hoisted out of the round loop).
    [SkipLocalsInit]
    private static void KeccakF(Span<Vector512<ulong>> a)
    {
        ref Vector512<ulong> s = ref MemoryMarshal.GetReference(a);
        ref ulong rc = ref MemoryMarshal.GetArrayDataReference(RoundConstants);
        ref Vector512<ulong> rho = ref MemoryMarshal.GetArrayDataReference(RhoBroadcasts);
        ref int src = ref MemoryMarshal.GetReference(SrcForB);

        Span<Vector512<ulong>> bScratch = stackalloc Vector512<ulong>[StateLanes];
        ref Vector512<ulong> b = ref MemoryMarshal.GetReference(bScratch);

        for (int round = 0; round < Rounds; round++)
        {
            // theta
            Vector512<ulong> c0 = Xor5(ref s, 0, 5, 10, 15, 20);
            Vector512<ulong> c1 = Xor5(ref s, 1, 6, 11, 16, 21);
            Vector512<ulong> c2 = Xor5(ref s, 2, 7, 12, 17, 22);
            Vector512<ulong> c3 = Xor5(ref s, 3, 8, 13, 18, 23);
            Vector512<ulong> c4 = Xor5(ref s, 4, 9, 14, 19, 24);

            Vector512<ulong> d0 = c4 ^ Rotl1(c1);
            Vector512<ulong> d1 = c0 ^ Rotl1(c2);
            Vector512<ulong> d2 = c1 ^ Rotl1(c3);
            Vector512<ulong> d3 = c2 ^ Rotl1(c4);
            Vector512<ulong> d4 = c3 ^ Rotl1(c0);

            for (int x = 0; x < 5; x++)
            {
                Vector512<ulong> d = x switch { 0 => d0, 1 => d1, 2 => d2, 3 => d3, _ => d4 };
                Get(ref s, x) ^= d;
                Get(ref s, x + 5) ^= d;
                Get(ref s, x + 10) ^= d;
                Get(ref s, x + 15) ^= d;
                Get(ref s, x + 20) ^= d;
            }

            // rho + pi: B[lane] = rotl(A[SrcForB[lane]], RhoForB[lane]); RhoBroadcasts holds each rho broadcast to 8 lanes.
            for (int lane = 0; lane < StateLanes; lane++)
            {
                Unsafe.Add(ref b, lane) = Avx512F.RotateLeftVariable(Get(ref s, Unsafe.Add(ref src, lane)), Unsafe.Add(ref rho, lane));
            }

            // chi: A[5y+x] = B[5y+x] ^ (~B[5y+(x+1)%5] & B[5y+(x+2)%5]).
            // Vector512.AndNot(l, r) = l & ~r, so ~B[x+1] & B[x+2] is AndNot(B[x+2], B[x+1]).
            for (int y = 0; y < 5; y++)
            {
                int r = 5 * y;
                Vector512<ulong> b0 = Unsafe.Add(ref b, r), b1 = Unsafe.Add(ref b, r + 1), b2 = Unsafe.Add(ref b, r + 2), b3 = Unsafe.Add(ref b, r + 3), b4 = Unsafe.Add(ref b, r + 4);
                Get(ref s, r + 0) = b0 ^ Vector512.AndNot(b2, b1);
                Get(ref s, r + 1) = b1 ^ Vector512.AndNot(b3, b2);
                Get(ref s, r + 2) = b2 ^ Vector512.AndNot(b4, b3);
                Get(ref s, r + 3) = b3 ^ Vector512.AndNot(b0, b4);
                Get(ref s, r + 4) = b4 ^ Vector512.AndNot(b1, b0);
            }

            // iota: A[0,0] ^= RC[round], broadcast to all 8 lanes.
            Get(ref s, 0) ^= Vector512.Create(Unsafe.Add(ref rc, round));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref Vector512<ulong> Get(ref Vector512<ulong> s, int lane) => ref Unsafe.Add(ref s, lane);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> Xor5(ref Vector512<ulong> s, int a, int b, int c, int d, int e) =>
        Unsafe.Add(ref s, a) ^ Unsafe.Add(ref s, b) ^ Unsafe.Add(ref s, c) ^ Unsafe.Add(ref s, d) ^ Unsafe.Add(ref s, e);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> Rotl1(Vector512<ulong> v) => Avx512F.RotateLeft(v, 1);

    // Writes lanes 0..3 (little-endian) of each selected element as its 32-byte keccak256 digest.
    private static void WriteDigests(ReadOnlySpan<Vector512<ulong>> state, Span<ValueHash256> outputs, ReadOnlySpan<int> idx, int activeMask)
    {
        // The digest is state words 0..3; extract element j of each of the first four lanes into the little-endian output.
        Span<ulong> digest = stackalloc ulong[4];
        for (int j = 0; j < Width; j++)
        {
            if ((activeMask & (1 << j)) == 0) continue;

            digest[0] = state[0][j];
            digest[1] = state[1][j];
            digest[2] = state[2][j];
            digest[3] = state[3][j];
            outputs[idx[j]] = new ValueHash256(MemoryMarshal.AsBytes(digest));
        }
    }

    // Per-message fallback for group remainders (<8) and the non-accelerated path. When `contiguous`, hashes messages
    // in original order [from, to); otherwise hashes the permutation entries [from, to).
    private static void HashPerMessage(
        ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs,
        ReadOnlySpan<int> permutation, int from, bool contiguous, int to = -1)
    {
        if (contiguous)
        {
            int start = from == 0 ? 0 : offsets[from - 1];
            int end = to < 0 ? offsets.Length : to;
            for (int i = from; i < end; i++)
            {
                int e = offsets[i];
                HashOne(flat.Slice(start, e - start), ref outputs[i]);
                start = e;
            }
            return;
        }

        for (int p = from; p < to; p++)
        {
            int m = permutation[p];
            int start = m == 0 ? 0 : offsets[m - 1];
            HashOne(flat.Slice(start, offsets[m] - start), ref outputs[m]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashOne(ReadOnlySpan<byte> input, ref ValueHash256 output) =>
        KeccakHash.ComputeHash(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref output, 1)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUlong(ReadOnlySpan<byte> src, int at) =>
        BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(at, sizeof(ulong)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BlockCountOf(ReadOnlySpan<int> offsets, int message)
    {
        int start = message == 0 ? 0 : offsets[message - 1];
        return KeccakBatchGrouping.BlockCount(offsets[message] - start);
    }

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

    // Block counts fit on the stack for realistic batches; heap above this.
    private const int StackThreshold = 1024;

    // pi permutation, precomputed: SrcForB[dst] = source A lane whose rotated value lands in B[dst].
    private static ReadOnlySpan<int> SrcForB =>
        [0, 6, 12, 18, 24, 3, 9, 10, 16, 22, 1, 7, 13, 19, 20, 4, 5, 11, 17, 23, 2, 8, 14, 15, 21];

    // rho[SrcForB[dst]] broadcast to all 8 lanes, as RotateLeftVariable counts (all elements rotate by the same rho).
    private static readonly Vector512<ulong>[] RhoBroadcasts = BuildRhoBroadcasts();

    private static Vector512<ulong>[] BuildRhoBroadcasts()
    {
        ReadOnlySpan<int> rho = [0, 44, 43, 21, 14, 28, 20, 3, 45, 61, 1, 6, 25, 8, 18, 27, 36, 10, 15, 56, 62, 55, 39, 41, 2];
        Vector512<ulong>[] result = new Vector512<ulong>[StateLanes];
        for (int i = 0; i < StateLanes; i++) result[i] = Vector512.Create((ulong)rho[i]);
        return result;
    }

    private static void ThrowInvalidOffsets() =>
        throw new ArgumentException("offsets must be non-decreasing and within the flat input bounds.");

    private static void ThrowLengthMismatch() =>
        throw new ArgumentException("offsets and outputs must have equal length.");

    private static void ThrowLastOffsetMismatch() =>
        throw new ArgumentException("Last offset must equal the flat input length.");
}
