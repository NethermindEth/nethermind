// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class TxTraceFilterTests
{
    [Test]
    public void Trace_filter_should_filter_proper_traces()
    {
        ParityTraceAction action1 = new() { From = TestItem.AddressA, To = TestItem.AddressB };
        ParityTraceAction action2 = new() { From = TestItem.AddressB, To = TestItem.AddressC };
        ParityTraceAction action3 = new() { From = TestItem.AddressA, To = TestItem.AddressC };

        TxTraceFilter filterForFrom = new(new[] { TestItem.AddressA }, null, 0, null);
        Assert.AreEqual(true, filterForFrom.ShouldUseTxTrace(action1));
        Assert.AreEqual(false, filterForFrom.ShouldUseTxTrace(action2));
        Assert.AreEqual(true, filterForFrom.ShouldUseTxTrace(action3));

        TxTraceFilter filterForTo = new(null, new[] { TestItem.AddressC }, 0, null);
        Assert.AreEqual(false, filterForTo.ShouldUseTxTrace(action1));
        Assert.AreEqual(true, filterForTo.ShouldUseTxTrace(action2));
        Assert.AreEqual(true, filterForTo.ShouldUseTxTrace(action3));

        TxTraceFilter filterForFromAndTo = new(new[] { TestItem.AddressA }, new[] { TestItem.AddressC }, 0, null);
        Assert.AreEqual(false, filterForFromAndTo.ShouldUseTxTrace(action1));
        Assert.AreEqual(false, filterForFromAndTo.ShouldUseTxTrace(action2));
        Assert.AreEqual(true, filterForFromAndTo.ShouldUseTxTrace(action3));
    }

    [Test]
    public void Trace_filter_should_skip_expected_number_of_traces_()
    {
        TxTraceFilter traceFilter = new(new[] { TestItem.AddressA }, null, 2, 2);
        ParityTraceAction action1 = new() { From = TestItem.AddressA };
        ParityTraceAction action2 = new() { From = TestItem.AddressB };

        Assert.AreEqual(false, traceFilter.ShouldUseTxTrace(action1));
        Assert.AreEqual(false, traceFilter.ShouldUseTxTrace(action2));
        Assert.AreEqual(false, traceFilter.ShouldUseTxTrace(action1));
        Assert.AreEqual(false, traceFilter.ShouldUseTxTrace(action2));
        Assert.AreEqual(true, traceFilter.ShouldUseTxTrace(action1));
        Assert.AreEqual(true, traceFilter.ShouldUseTxTrace(action1));
        Assert.AreEqual(false, traceFilter.ShouldUseTxTrace(action1));

    }
}
