// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>The guest's limb-wise <see cref="EvmStack.Swap{TTracingInst, TCheckDepth}"/> for SWAP1 to SWAP16.</summary>
public class GuestStackSwapTests
{
    private const int Words = 17;

    [Test]
    public void Swap_exchanges_the_top_with_the_word_at_depth_and_leaves_the_rest([Range(2, Words)] int depth)
    {
        byte[] buffer = new byte[(Words + 1) * EvmStack.WordSize];
        EvmStack stack = new(0, ref buffer[0], ReadOnlySpan<byte>.Empty, null);
        // Every limb distinct, so a limb written to the wrong word or the wrong position shows up.
        for (int i = 0; i < Words; i++)
        {
            UInt256 word = Word(i);
            stack.PushUInt256<OffFlag>(in word);
        }

        Assert.That(stack.Swap<OffFlag, OnFlag>(depth), Is.EqualTo(EvmExceptionType.None));

        // Word i was pushed i-th, so it sits at depth Words - i; the top is depth 1.
        for (int d = 1; d <= Words; d++)
        {
            Assert.That(stack.PopUInt256(out UInt256 popped), Is.True);
            int expected = d == 1 ? Words - depth : d == depth ? Words - 1 : Words - d;
            Assert.That(popped, Is.EqualTo(Word(expected)), $"depth {d}");
        }
    }

    [Test]
    public void Swap_below_the_stack_underflows()
    {
        byte[] buffer = new byte[2 * EvmStack.WordSize];
        EvmStack stack = new(0, ref buffer[0], ReadOnlySpan<byte>.Empty, null);
        UInt256 word = Word(0);
        stack.PushUInt256<OffFlag>(in word);

        Assert.That(stack.Swap<OffFlag, OnFlag>(2), Is.EqualTo(EvmExceptionType.StackUnderflow));
    }

    private static UInt256 Word(int i)
    {
        ulong b = (ulong)i << 8;
        return new UInt256(b | 1, b | 2, b | 3, b | 4);
    }
}
