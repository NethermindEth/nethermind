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
}
