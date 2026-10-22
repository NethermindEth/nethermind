// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test;
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
            Assert.That(stack[i], Is.SequenceEqualTo(bigEndian.AsSpan(i * EvmStack.WordSize, EvmStack.WordSize)));
            Assert.That(stack.PeekUInt256(Words.Length - 1 - i), Is.EqualTo(Words[i]));
        }
    }

    [Test]
    public void CopyRawBytesTo_writes_the_same_bytes_as_ToRawBytes_into_a_dirty_buffer()
    {
        byte[] bigEndian = new byte[Words.Length * EvmStack.WordSize];
        for (int i = 0; i < Words.Length; i++)
        {
            Words[i].ToBigEndian(bigEndian.AsSpan(i * EvmStack.WordSize, EvmStack.WordSize));
        }

        TraceStack stack = new(bigEndian);
        byte[] buffer = new byte[stack.RawByteLength + EvmStack.WordSize];
        buffer.AsSpan().Fill(0xff);

        stack.CopyRawBytesTo(buffer);

        Assert.That(buffer.AsSpan(0, stack.RawByteLength).ToArray(), Is.EqualTo(stack.ToRawBytes()));
    }

    [TestCase(0, 0)]
    [TestCase(32, 32)]
    [TestCase(64, 40)]
    [TestCase(64, 0)]
    public void TraceMemory_CopyRawWordBytesTo_writes_the_same_bytes_as_ToRawWordBytes_into_a_dirty_buffer(int size, int available)
    {
        byte[] memory = new byte[available];
        for (int i = 0; i < memory.Length; i++) memory[i] = (byte)(i + 1);
        TraceMemory trace = new((ulong)size, memory);
        byte[] buffer = new byte[trace.RawWordByteLength + EvmStack.WordSize];
        buffer.AsSpan().Fill(0xff);

        trace.CopyRawWordBytesTo(buffer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.RawWordByteLength, Is.EqualTo(trace.ToRawWordBytes().Length));
            Assert.That(buffer.AsSpan(0, trace.RawWordByteLength).ToArray(), Is.EqualTo(trace.ToRawWordBytes()));
        }
    }
}
