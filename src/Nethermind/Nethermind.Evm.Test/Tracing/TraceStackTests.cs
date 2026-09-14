// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[Parallelizable(ParallelScope.Self)]
public class TraceStackTests
{
    private static readonly UInt256[] Words = [1, UInt256.MaxValue, new UInt256(1, 2, 3, 4)];

    [Test]
    public void Big_endian_words_read_back_as_pushed()
    {
        byte[] bigEndian = new byte[Words.Length * EvmStack.WordSize];
        for (int i = 0; i < Words.Length; i++)
        {
            Words[i].ToBigEndian(bigEndian.AsSpan(i * EvmStack.WordSize, EvmStack.WordSize));
        }

        TraceStack stack = new(bigEndian);

        Assert.That(stack.Count, Is.EqualTo(Words.Length));
        Assert.That(stack.ToRawBytes(), Is.EqualTo(bigEndian));
        for (int i = 0; i < Words.Length; i++)
        {
            Assert.That(stack[i].Span.ToArray(), Is.EqualTo(bigEndian.AsSpan(i * EvmStack.WordSize, EvmStack.WordSize).ToArray()));
            Assert.That(stack.PeekUInt256(Words.Length - 1 - i), Is.EqualTo(Words[i]));
        }
    }
}
