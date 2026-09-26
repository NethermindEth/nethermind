// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class TxTraceFilterTests
{
    [Test]
    public void Trace_filter_should_filter_proper_traces()
    {
        ParityTraceAction action1 = new() { From = TestItem.AddressA, To = TestItem.AddressB };
        ParityTraceAction action2 = new() { From = TestItem.AddressB, To = TestItem.AddressC };
        ParityTraceAction action3 = new() { From = TestItem.AddressA, To = TestItem.AddressC };
        ParityTraceAction reward = new() { Type = "reward", Author = TestItem.AddressC };

        TxTraceFilter filterForFrom = new(new[] { TestItem.AddressA }, null, 0, null);
        Assert.That(filterForFrom.ShouldUseTxTrace(action1), Is.EqualTo(true));
        Assert.That(filterForFrom.ShouldUseTxTrace(action2), Is.EqualTo(false));
        Assert.That(filterForFrom.ShouldUseTxTrace(action3), Is.EqualTo(true));
        Assert.That(filterForFrom.ShouldUseTxTrace(reward), Is.EqualTo(false));

        TxTraceFilter filterForTo = new(null, new[] { TestItem.AddressC }, 0, null);
        Assert.That(filterForTo.ShouldUseTxTrace(action1), Is.EqualTo(false));
        Assert.That(filterForTo.ShouldUseTxTrace(action2), Is.EqualTo(true));
        Assert.That(filterForTo.ShouldUseTxTrace(action3), Is.EqualTo(true));
        Assert.That(filterForTo.ShouldUseTxTrace(reward), Is.EqualTo(true));

        TxTraceFilter filterForFromAndTo = new(new[] { TestItem.AddressA }, new[] { TestItem.AddressC }, 0, null);
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(action1), Is.EqualTo(false));
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(action2), Is.EqualTo(false));
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(action3), Is.EqualTo(true));
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(reward), Is.EqualTo(false));
    }

    [Test]
    public void Trace_filter_should_skip_expected_number_of_traces_()
    {
        TxTraceFilter traceFilter = new(new[] { TestItem.AddressA }, null, 2, 2);
        ParityTraceAction action1 = new() { From = TestItem.AddressA };
        ParityTraceAction action2 = new() { From = TestItem.AddressB };

        Assert.That(traceFilter.ShouldUseTxTrace(action1), Is.EqualTo(false));
        Assert.That(traceFilter.ShouldUseTxTrace(action2), Is.EqualTo(false));
        Assert.That(traceFilter.ShouldUseTxTrace(action1), Is.EqualTo(false));
        Assert.That(traceFilter.ShouldUseTxTrace(action2), Is.EqualTo(false));
        Assert.That(traceFilter.ShouldUseTxTrace(action1), Is.EqualTo(true));
        Assert.That(traceFilter.ShouldUseTxTrace(action1), Is.EqualTo(true));
        Assert.That(traceFilter.ShouldUseTxTrace(action1), Is.EqualTo(false));

    }

    [TestCase(2, 2, false, TestName = "CountLeft")]
    [TestCase(2, 3, true, TestName = "CountReached")]
    [TestCase(null, 3, false, TestName = "NoCount")]
    [TestCase(0, 0, true, TestName = "ZeroCount")]
    public void IsExhausted_WhenMatchesAreConsumed_ReportsWhetherAnotherTraceCanBeAccepted(int? count, int matches, bool expected)
    {
        TxTraceFilter traceFilter = new(null, null, 1, count);
        ParityTraceAction action = new() { From = TestItem.AddressA };
        for (int i = 0; i < matches; i++)
        {
            traceFilter.ShouldUseTxTrace(action);
        }

        Assert.That(traceFilter.IsExhausted, Is.EqualTo(expected), "the filter is exhausted exactly when count traces were accepted after skipping after");
    }
}
