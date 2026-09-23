// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[Parallelizable(ParallelScope.All)]
public class CompositeTxTracerTests
{
    [Test]
    public void InstructionMask_WhenSiblingNeedsSnapshots_RequiresFullTracing(
        [Values] bool stack, [Values] bool memory, [Values] bool returnData)
    {
        ITxTracer filtered = Substitute.For<ITxTracer, IInstructionTracingFilter>();
        filtered.IsTracingInstructions.Returns(true);
        ((IInstructionTracingFilter)filtered).InstructionMask.Returns(UInt256.One);
        ITxTracer observer = Substitute.For<ITxTracer>();
        observer.IsTracingStack.Returns(stack);
        observer.IsTracingMemory.Returns(memory);
        observer.IsTracingReturnData.Returns(returnData);
        using CompositeTxTracer tracer = new(filtered, observer);

        Assert.That(tracer.InstructionMask, Is.EqualTo(stack || memory || returnData ? UInt256.MaxValue : UInt256.One));
    }

    [Test]
    public void InstructionMask_WhenCancellationForcesSnapshots_RequiresFullTracing(
        [Values] bool stack, [Values] bool memory, [Values] bool returnData, [Values] bool instructions)
    {
        ITxTracer filtered = Substitute.For<ITxTracer, IInstructionTracingFilter>();
        filtered.IsTracingInstructions.Returns(true);
        ((IInstructionTracingFilter)filtered).InstructionMask.Returns(UInt256.One);
        using CancellationTxTracer tracer = new(filtered)
        {
            IsTracingStack = stack,
            IsTracingMemory = memory,
            IsTracingReturnData = returnData,
            IsTracingInstructions = instructions,
        };

        Assert.That(tracer.InstructionMask, Is.EqualTo(stack || memory || returnData || instructions ? UInt256.MaxValue : UInt256.One));
    }

    [Test]
    public void StartOperation_WhenRequirementsChange_PreservesOtherTracers(
        [Values] bool otherStack, [Values] bool otherMemory)
    {
        ITxTracer changing = Substitute.For<ITxTracer>();
        changing.IsTracingInstructions.Returns(true);
        changing.IsTracingStack.Returns(true);
        changing.IsTracingMemory.Returns(true);
        changing.When(tracer => tracer.StartOperation(0, Instruction.ADD, 100, null!)).Do(_ =>
        {
            changing.IsTracingStack.Returns(false);
            changing.IsTracingMemory.Returns(false);
        });
        ITxTracer other = Substitute.For<ITxTracer>();
        other.IsTracingStack.Returns(otherStack);
        other.IsTracingMemory.Returns(otherMemory);
        using CompositeTxTracer tracer = new(new CompositeTxTracer(changing), other);

        tracer.StartOperation(0, Instruction.ADD, 100, null!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.IsTracingStack, Is.EqualTo(otherStack));
            Assert.That(tracer.IsTracingMemory, Is.EqualTo(otherMemory));
        }
    }

    [Test]
    public void Forwards_action_gas_only_to_action_tracers([Values] bool tracingActions, [Values] bool cancellation)
    {
        ITxTracer inner = Substitute.For<ITxTracer>();
        inner.IsTracingActions.Returns(tracingActions);
        using ITxTracer tracer = cancellation ? new CancellationTxTracer(inner) : new CompositeTxTracer(inner);

        tracer.ReportActionRemainingGas(1234);

        inner.Received(tracingActions ? 1 : 0).ReportActionRemainingGas(1234);
    }

    [Test]
    public void Aggregates_receipt_log_requirements([Values] bool firstRequiresLogs, [Values] bool secondRequiresLogs)
    {
        ITxTracer first = Substitute.For<ITxTracer>();
        first.IsCollectingLogs.Returns(firstRequiresLogs);
        ITxTracer second = Substitute.For<ITxTracer>();
        second.IsCollectingLogs.Returns(secondRequiresLogs);
        using CompositeTxTracer tracer = new(first, second);

        Assert.That(tracer.IsCollectingLogs, Is.EqualTo(firstRequiresLogs || secondRequiresLogs));
    }

    [Test]
    public void Cancellation_preserves_receipt_log_requirements([Values] bool innerRequiresLogs, [Values] bool forceReceipts)
    {
        ITxTracer inner = Substitute.For<ITxTracer>();
        inner.IsTracingReceipt.Returns(true);
        inner.IsCollectingLogs.Returns(innerRequiresLogs);
        using CancellationTxTracer tracer = new(inner) { IsTracingReceipt = forceReceipts };

        Assert.That(tracer.IsCollectingLogs, Is.EqualTo(innerRequiresLogs || forceReceipts));
    }

    [Test]
    public void Aggregates_IsCancelable_from_children()
    {
        CompositeTxTracer nonCancelable = new(Substitute.For<ITxTracer>(), Substitute.For<ITxTracer>());
        Assert.That(nonCancelable.IsCancelable, Is.False);

        using CancellationTokenSource cts = new();
        CompositeTxTracer cancelable = new(Substitute.For<ITxTracer>(), new CancellationTxTracer(Substitute.For<ITxTracer>(), cts.Token));
        Assert.That(cancelable.IsCancelable, Is.True);
    }

    [Test]
    public void Forwards_IsCancelled_to_a_nested_cancelable_child()
    {
        using CancellationTokenSource cts = new();
        CompositeTxTracer tracer = new(Substitute.For<ITxTracer>(), new CancellationTxTracer(Substitute.For<ITxTracer>(), cts.Token));

        Assert.That(tracer.IsCancelled, Is.False);
        cts.Cancel();
        Assert.That(tracer.IsCancelled, Is.True);
    }

    [Test]
    public void Wrapping_tracer_implements_every_meaningful_default_interface_member([Values(typeof(CompositeTxTracer), typeof(CancellationTxTracer))] Type wrapperType)
    {
        string[] convenienceForwarders = ["get_IsTracing", "ReportStackPush", "ReportMemoryChange"];

        MethodInfo[] defaultMembers = typeof(ITxTracer)
            .GetMethods()
            .Where(m => !m.IsAbstract && !m.IsStatic && !convenienceForwarders.Contains(m.Name))
            .ToArray();

        Assert.That(defaultMembers.Select(m => m.Name), Is.SupersetOf(["get_IsCancelable", "get_IsCancelled"]),
            "the fitness scan must cover the cancellation members it exists to protect");

        InterfaceMapping map = wrapperType.GetInterfaceMap(typeof(ITxTracer));

        string[] inheritedDefaults = defaultMembers
            .Where(member =>
            {
                int i = Array.IndexOf(map.InterfaceMethods, member);
                return i >= 0 && map.TargetMethods[i].DeclaringType == typeof(ITxTracer);
            })
            .Select(m => m.Name)
            .ToArray();

        Assert.That(inheritedDefaults, Is.Empty,
            $"{wrapperType.Name} silently inherits the default of {string.Join(", ", inheritedDefaults)}; an aggregating tracer must implement it so it is not dropped when nested.");
    }
}
