// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class EvmWordExtensionsTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(0x5a)]
    [TestCase(0xff)]
    public void ByteSwap_reverses_deterministic_bytes_and_round_trips(int seed)
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
}
