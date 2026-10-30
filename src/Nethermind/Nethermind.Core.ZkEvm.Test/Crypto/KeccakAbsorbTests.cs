// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Crypto;

/// <summary>
/// The guest's sub-rate absorb, checked against the padded block before the permutation, which is a zkVM
/// precompile no host process can call.
/// </summary>
public class KeccakAbsorbTests
{
    private const int RateBytes = 136;
    private const int StateBytes = 200;

    [Test]
    public unsafe void Short_absorb_zeroes_the_state_and_writes_the_message_and_its_pad_byte([Range(8, RateBytes - 1)] int length)
    {
        byte[] input = new byte[length];
        new Random(length).NextBytes(input);
        byte[] expected = new byte[StateBytes];
        input.CopyTo(expected, 0);
        expected[length] = 0x01;

        ulong[] lanes = new ulong[StateBytes / sizeof(ulong)];
        lanes.AsSpan().Fill(ulong.MaxValue);
        fixed (byte* data = input)
        {
            KeccakHash.AbsorbShortFixed(ref lanes[0], data, (nuint)length);
        }

        Assert.That(MemoryMarshal.AsBytes(lanes.AsSpan()).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public unsafe void Block_copy_moves_unaligned_bytes_down_and_absorbs_them(
        [Range(1, 7)] int offset, [Values(0, 1, 17)] int gapWords, [Values(1, 2)] int blocks)
    {
        const int rateLanes = RateBytes / sizeof(ulong);
        // The last block reads one source word past itself, which holds its last bytes.
        ulong[] buffer = new ulong[gapWords + blocks * rateLanes + 1];
        new Random(offset * 100 + gapWords * 10 + blocks).NextBytes(MemoryMarshal.AsBytes(buffer.AsSpan()));
        byte[] source = MemoryMarshal.AsBytes(buffer.AsSpan()).Slice(gapWords * sizeof(ulong) + offset, blocks * RateBytes).ToArray();

        ulong[] expectedLanes = new ulong[rateLanes];
        for (int block = 0; block < blocks; block++)
        {
            ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(source.AsSpan(block * RateBytes, RateBytes));
            for (int i = 0; i < rateLanes; i++) expectedLanes[i] ^= words[i];
        }

        ulong[] lanes = new ulong[StateBytes / sizeof(ulong)];
        lanes.AsSpan().Fill(ulong.MaxValue);
        fixed (ulong* start = buffer)
        {
            ulong* to = start;
            ulong* from = start + gapWords;
            ulong low = *from;
            for (int block = 0; block < blocks; block++, from += rateLanes, to += rateLanes)
            {
                low = KeccakHash.CopyAndAbsorbBlock(ref lanes[0], from, to, low, offset * 8, intoZeroState: block == 0);
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MemoryMarshal.AsBytes(buffer.AsSpan(0, blocks * rateLanes)).ToArray(), Is.EqualTo(source));
            Assert.That(lanes.AsSpan(0, rateLanes).ToArray(), Is.EqualTo(expectedLanes));
        }
    }

    [Test]
    public void Partial_lane_zero_extends_only_its_bytes([Range(0, 7)] int partial)
    {
        byte[] lane = new byte[sizeof(ulong)];
        new Random(partial).NextBytes(lane);
        byte[] expected = new byte[sizeof(ulong)];
        lane.AsSpan(0, partial).CopyTo(expected);

        ulong actual = KeccakHash.PartialLane(ref lane[0], (nuint)partial);

        Assert.That(actual, Is.EqualTo(BitConverter.ToUInt64(expected)));
    }
}
