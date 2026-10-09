// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Shared packing helpers for the Keccak AVX2 batch kernels, which require caller-applied padding.
/// </summary>
/// <remarks>
/// Convention read from <c>KeccakHash.avx2x4.cs</c> and confirmed against
/// <c>Nethermind.Core.Test/KeccakTests.cs</c>: a batch call takes <c>laneCount</c> equal-length
/// messages laid out back to back in one buffer, stride == the per-lane padded length in bytes.
/// Each lane must already carry FIPS 202 multi-rate padding: byte <c>message.Length</c> gets 0x01,
/// and the last byte of the padded lane is ORed with 0x80 (the two collapse into 0x81 when they
/// land on the same byte, which happens whenever the message fills the lane up to its last byte).
/// <c>ComputePaddedBlocks4Avx2</c> / <c>ComputePaddedBlocks8Avx512</c> fix the per-lane length at
/// exactly one 136-byte rate block (<c>KeccakHash</c>'s private <c>HASH_DATA_AREA</c>);
/// <c>ComputePaddedMultiBlocks4Avx2</c> / <c>ComputePaddedMultiBlocks8Avx512</c> take a
/// caller-chosen padded length per lane (a positive multiple of 136), with the 0x01/0x80 padding
/// applied only inside the final 136-byte block. By contrast, the fixed 532-byte kernels
/// (<c>ComputeHash532Bytes2Avx512VL</c>, <c>ComputeHash532Bytes8Avx512</c>) take raw, unpadded
/// 532-byte messages directly: their padding is baked in for that one input length.
/// </remarks>
internal static class KeccakPacking
{
    /// <summary>Copies <paramref name="message"/> into <paramref name="destination"/> and applies
    /// Keccak-256 multi-rate padding in the unused tail (0x01 right after the message, 0x80 ORed
    /// into the last byte).</summary>
    /// <remarks><paramref name="message"/> must be strictly shorter than <paramref name="destination"/>
    /// so there is room for the 0x01 terminator byte.</remarks>
    internal static void PackPaddedLane(Span<byte> destination, ReadOnlySpan<byte> message)
    {
        message.CopyTo(destination);
        destination[message.Length..].Clear();
        destination[message.Length] |= 0x01;
        destination[^1] |= 0x80;
    }
}

/// <summary>
/// Case A: the 4-way AVX2 batch kernel versus 1-4 sequential single-message hashes, at message
/// sizes that fill one to four 136-byte Keccak rate blocks. Finds the minimum lane count at which
/// batching pays on an AVX2-only machine.
/// </summary>
/// <remarks>
/// Both benchmarks include packing/padding, kernel or scalar dispatch, and writing the digest(s)
/// to the output buffer. <see cref="Batch4Avx2"/> always performs one full 4-lane kernel call
/// regardless of <see cref="Count"/>, so its cost at a given <see cref="Size"/> is constant across
/// rows: comparing it against <see cref="Sequential"/> at each <see cref="Count"/> shows the fill
/// level at which the wasted lanes stop mattering.
/// </remarks>
public class KeccakBatchBenchmarkSequentialVsBatch
{
    private const int LaneCount = 4;
    private const int HashSize = 32;
    private const int RateBlock = 136; // KeccakHash's private HASH_DATA_AREA; Keccak-256's rate in bytes.

    private byte[][] _messages = null!;
    private byte[] _packedBatch = null!;
    private byte[] _sequentialOutput = null!;
    private byte[] _batchOutput = null!;

    /// <summary>Padded lane length in bytes: 136, 272, 408 or 544 (one to four rate blocks).</summary>
    [Params(136, 272, 408, 544)]
    public int Size { get; set; }

    /// <summary>How many of the four lanes carry a real message the caller needs.</summary>
    [Params(1, 2, 3, 4)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _messages = new byte[LaneCount][];
        for (int lane = 0; lane < LaneCount; lane++)
        {
            // One byte short of Size so the message needs exactly Size / RateBlock padded blocks.
            _messages[lane] = new byte[Size - 1];
            random.NextBytes(_messages[lane]);
        }

        _packedBatch = new byte[LaneCount * Size];
        _sequentialOutput = new byte[LaneCount * HashSize];
        _batchOutput = new byte[LaneCount * HashSize];
    }

    [Benchmark(Baseline = true)]
    public void Sequential()
    {
        for (int lane = 0; lane < Count; lane++)
        {
            KeccakHash.ComputeHash(_messages[lane], _sequentialOutput.AsSpan(lane * HashSize, HashSize));
        }
    }

    [Benchmark]
    public void Batch4Avx2()
    {
        if (!Avx2.IsSupported) return;

        for (int lane = 0; lane < LaneCount; lane++)
        {
            KeccakPacking.PackPaddedLane(_packedBatch.AsSpan(lane * Size, Size), _messages[lane]);
        }

        if (Size == RateBlock)
            KeccakHash.ComputePaddedBlocks4Avx2(ref _packedBatch[0], ref _batchOutput[0]);
        else
            KeccakHash.ComputePaddedMultiBlocks4Avx2(ref _packedBatch[0], Size, ref _batchOutput[0]);
    }
}

/// <summary>
/// Case B: the AVX-512VL pair kernel, the 4-way AVX2 kernel and the 8-way AVX-512 kernel, each
/// processing the same 1-8 fixed 532-byte messages (the RLP-hashing shape used by
/// <c>TrieNode.Decoder.cs</c>). Compares the three SIMD widths at partial and full batch fills.
/// </summary>
/// <remarks>
/// Every benchmark includes packing (a plain copy for the two fixed-length AVX-512 kernels, whose
/// padding is baked in for 532-byte inputs; copy-and-pad for the AVX2 path, which needs the
/// message padded to <see cref="KeccakHash.Hash532PaddedLength"/>), kernel dispatch, and writing
/// the digests to the output buffer. Each kernel always runs at its native lane width, so at a
/// <see cref="Fill"/> that is not a multiple of that width, some of the computed digests are
/// unused: this is what "batch fill" cost means here. AVX-512 cases no-op cleanly when the
/// required intrinsic is unavailable, rather than fault on unsupported hardware.
/// </remarks>
public class KeccakBatchBenchmarkWideFill
{
    private const int HashSize = 32;
    private const int FixedLength = KeccakHash.Hash532InputLength; // 532
    private static readonly int s_paddedLength = KeccakHash.Hash532PaddedLength; // 544

    private byte[][] _messages = null!;
    private byte[] _vl2Buffer = null!;
    private byte[] _avx2x4Buffer = null!;
    private byte[] _avx512x8Buffer = null!;
    private byte[] _output = null!;

    /// <summary>How many of the eight preallocated 532-byte messages are real work for this row.</summary>
    [Params(1, 2, 3, 4, 5, 6, 7, 8)]
    public int Fill { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _messages = new byte[8][];
        for (int lane = 0; lane < 8; lane++)
        {
            _messages[lane] = new byte[FixedLength];
            random.NextBytes(_messages[lane]);
        }

        _vl2Buffer = new byte[2 * FixedLength];
        _avx2x4Buffer = new byte[4 * s_paddedLength];
        _avx512x8Buffer = new byte[8 * FixedLength];
        _output = new byte[8 * HashSize];
    }

    [Benchmark]
    public void Sequential()
    {
        for (int lane = 0; lane < Fill; lane++)
        {
            KeccakHash.ComputeHash(_messages[lane], _output.AsSpan(lane * HashSize, HashSize));
        }
    }

    [Benchmark(Baseline = true)]
    public void Avx2FourWay()
    {
        if (!Avx2.IsSupported) return;

        for (int lane = 0; lane < Fill; lane += 4)
        {
            for (int i = 0; i < 4; i++)
            {
                KeccakPacking.PackPaddedLane(
                    _avx2x4Buffer.AsSpan(i * s_paddedLength, s_paddedLength), _messages[lane + i]);
            }

            KeccakHash.ComputePaddedMultiBlocks4Avx2(ref _avx2x4Buffer[0], s_paddedLength, ref _output[lane * HashSize]);
        }
    }

    [Benchmark]
    public void Avx512Vl2Pair()
    {
        if (!Avx512F.VL.IsSupported) return;

        int lane = 0;
        for (; lane + 2 <= Fill; lane += 2)
        {
            _messages[lane].CopyTo(_vl2Buffer, 0);
            _messages[lane + 1].CopyTo(_vl2Buffer, FixedLength);
            KeccakHash.ComputeHash532Bytes2Avx512VL(ref _vl2Buffer[0], ref _vl2Buffer[FixedLength], ref _output[lane * HashSize]);
        }

        if (lane < Fill)
        {
            // No partial-pair kernel exists; a real caller falls back to the scalar path for an
            // odd leftover (see TrieNode.Decoder.cs HashPreparedBranchPairs).
            KeccakHash.ComputeHash(_messages[lane], _output.AsSpan(lane * HashSize, HashSize));
        }
    }

    [Benchmark]
    public void Avx512EightWay()
    {
        if (!Avx512F.IsSupported) return;

        for (int lane = 0; lane < Fill; lane += 8)
        {
            for (int i = 0; i < 8; i++)
            {
                _messages[lane + i].CopyTo(_avx512x8Buffer, i * FixedLength);
            }

            KeccakHash.ComputeHash532Bytes8Avx512(ref _avx512x8Buffer[0], ref _output[lane * HashSize]);
        }
    }
}

/// <summary>
/// Case C: for the 4-way AVX2 path, splits the caller-side packing (copying and Keccak-padding
/// messages into the kernel's contiguous layout) from the kernel dispatch itself (its internal
/// AVX2 gather, the 24-round permutation per block, and the output store), so a change to either
/// side can be attributed to the half of the cost it actually touches.
/// </summary>
/// <remarks>
/// <see cref="PackOnly"/> and <see cref="KernelOnly"/> are deliberately partial: summed, they
/// approximate the total cost that <see cref="KeccakBatchBenchmarkSequentialVsBatch.Batch4Avx2"/>
/// measures as one figure at the same <see cref="Size"/>. <see cref="KernelOnly"/> reuses a buffer
/// packed once in <see cref="Setup"/> (the kernel never mutates its input), so no packing cost
/// leaks into its timing.
/// </remarks>
public class KeccakBatchBenchmarkPackVsKernel
{
    private const int LaneCount = 4;
    private const int HashSize = 32;
    private const int RateBlock = 136;

    private byte[][] _messages = null!;
    private byte[] _packScratch = null!;
    private byte[] _prePacked = null!;
    private byte[] _output = null!;

    /// <summary>Padded lane length in bytes: 136, 272, 408 or 544 (one to four rate blocks).</summary>
    [Params(136, 272, 408, 544)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _messages = new byte[LaneCount][];
        for (int lane = 0; lane < LaneCount; lane++)
        {
            _messages[lane] = new byte[Size - 1];
            random.NextBytes(_messages[lane]);
        }

        _packScratch = new byte[LaneCount * Size];
        _prePacked = new byte[LaneCount * Size];
        for (int lane = 0; lane < LaneCount; lane++)
        {
            KeccakPacking.PackPaddedLane(_prePacked.AsSpan(lane * Size, Size), _messages[lane]);
        }

        _output = new byte[LaneCount * HashSize];
    }

    [Benchmark(Baseline = true)]
    public void PackOnly()
    {
        for (int lane = 0; lane < LaneCount; lane++)
        {
            KeccakPacking.PackPaddedLane(_packScratch.AsSpan(lane * Size, Size), _messages[lane]);
        }
    }

    [Benchmark]
    public void KernelOnly()
    {
        if (!Avx2.IsSupported) return;

        if (Size == RateBlock)
            KeccakHash.ComputePaddedBlocks4Avx2(ref _prePacked[0], ref _output[0]);
        else
            KeccakHash.ComputePaddedMultiBlocks4Avx2(ref _prePacked[0], Size, ref _output[0]);
    }
}

/// <summary>
/// Case D: the 4-way AVX2 kernel and the 8-way AVX-512 kernel against 1-8 sequential single-message
/// hashes, all processing a fixed single 136-byte Keccak rate block per lane - the shape
/// <c>Nethermind.State/KeyHashBatch.cs</c> batches through <see cref="KeccakHash.ComputePaddedBlocks4Avx2"/>
/// and <see cref="KeccakHash.ComputePaddedBlocks8Avx512"/>. <see cref="KeccakBatchBenchmarkWideFill"/>'s
/// cost ratios were measured at 532 bytes and do not transfer to this length, so it is measured here
/// directly.
/// </summary>
/// <remarks>
/// Every benchmark includes packing (copy-and-pad into the kernel's fixed-block layout), kernel or
/// scalar dispatch, and writing the digest(s) to the output buffer. Each kernel always runs at its
/// native lane width, so at a <see cref="Fill"/> that is not a multiple of that width, some of the
/// computed digests are unused, same as in <see cref="KeccakBatchBenchmarkWideFill"/>. There is no
/// AVX-512VL pair kernel at this length - <see cref="KeccakHash.ComputeHash532Bytes2Avx512VL"/> is
/// hard-coded to 532-byte input - so this case has no VL row. AVX-512 no-ops cleanly when the
/// required intrinsic is unavailable, rather than fault on unsupported hardware.
/// </remarks>
public class KeccakBatchBenchmarkFixed136Fill
{
    private const int HashSize = 32;
    private const int RateBlock = 136; // KeccakHash's private HASH_DATA_AREA; Keccak-256's rate in bytes.
    private const int MessageLength = RateBlock - 1; // One byte short of the rate so the 0x01/0x80 padding fits in the lane.

    private byte[][] _messages = null!;
    private byte[] _avx2x4Buffer = null!;
    private byte[] _avx512x8Buffer = null!;
    private byte[] _output = null!;

    /// <summary>How many of the eight preallocated 135-byte messages are real work for this row.</summary>
    [Params(1, 2, 3, 4, 5, 6, 7, 8)]
    public int Fill { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _messages = new byte[8][];
        for (int lane = 0; lane < 8; lane++)
        {
            _messages[lane] = new byte[MessageLength];
            random.NextBytes(_messages[lane]);
        }

        _avx2x4Buffer = new byte[4 * RateBlock];
        _avx512x8Buffer = new byte[8 * RateBlock];
        _output = new byte[8 * HashSize];
    }

    [Benchmark]
    public void Sequential()
    {
        for (int lane = 0; lane < Fill; lane++)
        {
            KeccakHash.ComputeHash(_messages[lane], _output.AsSpan(lane * HashSize, HashSize));
        }
    }

    [Benchmark(Baseline = true)]
    public void Avx2FourWay()
    {
        if (!Avx2.IsSupported) return;

        for (int lane = 0; lane < Fill; lane += 4)
        {
            for (int i = 0; i < 4; i++)
            {
                KeccakPacking.PackPaddedLane(_avx2x4Buffer.AsSpan(i * RateBlock, RateBlock), _messages[lane + i]);
            }

            KeccakHash.ComputePaddedBlocks4Avx2(ref _avx2x4Buffer[0], ref _output[lane * HashSize]);
        }
    }

    [Benchmark]
    public void Avx512EightWay()
    {
        if (!Avx512F.IsSupported) return;

        for (int lane = 0; lane < Fill; lane += 8)
        {
            for (int i = 0; i < 8; i++)
            {
                KeccakPacking.PackPaddedLane(_avx512x8Buffer.AsSpan(i * RateBlock, RateBlock), _messages[lane + i]);
            }

            KeccakHash.ComputePaddedBlocks8Avx512(ref _avx512x8Buffer[0], ref _output[lane * HashSize]);
        }
    }
}
