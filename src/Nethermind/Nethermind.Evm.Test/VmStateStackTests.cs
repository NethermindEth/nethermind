// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Pins the parent-frame stack's depth bound.
/// </summary>
/// <remarks>
/// The stack has a fixed capacity, so the EVM's call guards are what keep it in range: CALL and CREATE
/// both refuse a new frame once the current one is at <see cref="VirtualMachineStatics.MaxCallDepth"/>, so
/// the deepest frame is 1024 and the parents ever pushed are frames 0 to 1023. A capacity below that would
/// break legal deep calls; these tests fail if either side of that relationship moves.
/// </remarks>
public class VmStateStackTests
{
    private const int Capacity = VirtualMachineStatics.MaxCallDepth + 1;

    /// <summary>Parents pushed while the deepest legal frame executes.</summary>
    private const int DeepestLegalParentCount = VirtualMachineStatics.MaxCallDepth;

    [Test]
    public void Pops_frames_in_reverse_push_order()
    {
        VmStateStack<EthereumGasPolicy> stack = new(Capacity);
        VmState<EthereumGasPolicy>[] frames = Frames(8);

        foreach (VmState<EthereumGasPolicy> frame in frames)
        {
            stack.Push(frame);
        }

        for (int i = frames.Length - 1; i >= 0; i--)
        {
            Assert.That(stack.Pop(), Is.SameAs(frames[i]), $"frame at depth {i}");
        }
    }

    [Test]
    public void Holds_every_parent_the_call_depth_guards_allow()
    {
        VmStateStack<EthereumGasPolicy> stack = new(Capacity);
        VmState<EthereumGasPolicy>[] frames = Frames(DeepestLegalParentCount);

        foreach (VmState<EthereumGasPolicy> frame in frames)
        {
            stack.Push(frame);
        }

        Assert.That(stack.Pop(), Is.SameAs(frames[^1]));
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

    [Test]
    public void Pop_then_push_reuses_the_slot()
    {
        VmStateStack<EthereumGasPolicy> stack = new(Capacity);
        VmState<EthereumGasPolicy> first = new();
        VmState<EthereumGasPolicy> second = new();

        stack.Push(first);
        Assert.That(stack.Pop(), Is.SameAs(first));
        stack.Push(second);

        Assert.That(stack.Pop(), Is.SameAs(second));
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
