// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class EvmWordExtensionsTests
{
    [Test]
    public void ByteSwap_reverses_deterministic_bytes_and_round_trips([Values(0, 1, 0x5a, 0xff)] int seed)
    {
        byte[] input = new byte[32];
        for (int i = 0; i < input.Length; i++) input[i] = unchecked((byte)(seed + i));

        byte[] expectedBytes = (byte[])input.Clone();
        Array.Reverse(expectedBytes);

        EvmWord word = MemoryMarshal.Read<EvmWord>(input);
        EvmWord swapped = word.ByteSwap();
        EvmWord expected = MemoryMarshal.Read<EvmWord>(expectedBytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(swapped, Is.EqualTo(expected));
            Assert.That(swapped.ByteSwap(), Is.EqualTo(word));
        }
    }
    [Test]
    public void Minimal_big_endian_preserves_each_byte_boundary([Range(0, 32)] int length, [Values(1, 128, 255)] byte leadingByte)
    {
        byte[] expected = new byte[Math.Max(1, length)];
        for (int i = 0; i < length; i++) expected[i] = (byte)(i + 1);
        if (length != 0) expected[0] = leadingByte;
        UInt256 value = new(expected, isBigEndian: true);
        byte[] encoded = value.ToMinimalBigEndian();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(expected));
            Assert.That(new UInt256(encoded, isBigEndian: true), Is.EqualTo(value));
        }
    }
}
