// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class VmStateStackTests
{
    private const int Capacity = 3;

    [Test]
    public void Tracks_count_order_and_slot_reuse()
    {
        VmStateStack<EthereumGasPolicy> stack = new(Capacity);
        VmState<EthereumGasPolicy>[] frames = Frames(Capacity);

        foreach (VmState<EthereumGasPolicy> frame in frames)
        {
            stack.Push(frame);
        }

        for (int i = frames.Length - 1; i >= 0; i--)
        {
            Assert.That(stack.Pop(), Is.SameAs(frames[i]), $"frame at depth {i}");
            Assert.That(stack.Count, Is.EqualTo(i));
        }

        stack.Push(frames[0]);
        Assert.That(stack.Pop(), Is.SameAs(frames[0]));
    }

    [Test]
    public void Push_past_capacity_throws_rather_than_corrupting()
    {
        VmStateStack<EthereumGasPolicy> stack = new(Capacity);
        foreach (VmState<EthereumGasPolicy> frame in Frames(Capacity))
        {
            stack.Push(frame);
        }

        Assert.That(() => stack.Push(new VmState<EthereumGasPolicy>()), Throws.InvalidOperationException);
    }

    [Test]
    public void Pop_when_empty_throws()
    {
        VmStateStack<EthereumGasPolicy> stack = new(Capacity);

        Assert.That(() => stack.Pop(), Throws.InvalidOperationException);
    }

    // Never initialized, so `_isDisposed` stays set and the DEBUG finalizer does not report them.
    private static VmState<EthereumGasPolicy>[] Frames(int count)
    {
        VmState<EthereumGasPolicy>[] frames = new VmState<EthereumGasPolicy>[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = new VmState<EthereumGasPolicy>();
        }

        return frames;
    }
}
