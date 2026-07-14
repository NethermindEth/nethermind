// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[Parallelizable(ParallelScope.All)]
public class CompositeTxTracerTests
{
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

    [TestCase(typeof(CompositeTxTracer))]
    [TestCase(typeof(CancellationTxTracer))]
    public void Wrapping_tracer_implements_every_meaningful_default_interface_member(Type wrapperType)
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
