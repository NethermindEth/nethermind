// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
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

        TxTraceFilter filterForFrom = new(new[] { TestItem.AddressA }, null, 0, null, TraceFilterMode.Intersection);
        Assert.That(filterForFrom.ShouldUseTxTrace(action1), Is.EqualTo(true));
        Assert.That(filterForFrom.ShouldUseTxTrace(action2), Is.EqualTo(false));
        Assert.That(filterForFrom.ShouldUseTxTrace(action3), Is.EqualTo(true));
        Assert.That(filterForFrom.ShouldUseTxTrace(reward), Is.EqualTo(false));

        TxTraceFilter filterForTo = new(null, new[] { TestItem.AddressC }, 0, null, TraceFilterMode.Intersection);
        Assert.That(filterForTo.ShouldUseTxTrace(action1), Is.EqualTo(false));
        Assert.That(filterForTo.ShouldUseTxTrace(action2), Is.EqualTo(true));
        Assert.That(filterForTo.ShouldUseTxTrace(action3), Is.EqualTo(true));
        Assert.That(filterForTo.ShouldUseTxTrace(reward), Is.EqualTo(true));

        TxTraceFilter filterForFromAndTo = new(new[] { TestItem.AddressA }, new[] { TestItem.AddressC }, 0, null, TraceFilterMode.Intersection);
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(action1), Is.EqualTo(false));
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(action2), Is.EqualTo(false));
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(action3), Is.EqualTo(true));
        Assert.That(filterForFromAndTo.ShouldUseTxTrace(reward), Is.EqualTo(false));
    }

    private static readonly ParityTraceAction AToB = new() { From = TestItem.AddressA, To = TestItem.AddressB };
    private static readonly ParityTraceAction BToC = new() { From = TestItem.AddressB, To = TestItem.AddressC };
    private static readonly ParityTraceAction AToC = new() { From = TestItem.AddressA, To = TestItem.AddressC };
    private static readonly ParityTraceAction BToB = new() { From = TestItem.AddressB, To = TestItem.AddressB };
    private static readonly ParityTraceAction RewardToC = new() { Type = "reward", Author = TestItem.AddressC };

    private static IEnumerable<TestCaseData> ModeCases()
    {
        Address[] a = [TestItem.AddressA];
        Address[] c = [TestItem.AddressC];
        // Expected matches for AToB, BToC, AToC, BToB, RewardToC.
        yield return new TestCaseData(TraceFilterMode.Union, a, c, new[] { true, true, true, false, true }).SetName("union of both lists");
        yield return new TestCaseData(TraceFilterMode.Union, a, null, new[] { true, false, true, false, false }).SetName("union with sender list only");
        yield return new TestCaseData(TraceFilterMode.Union, null, c, new[] { false, true, true, false, true }).SetName("union with recipient list only");
        yield return new TestCaseData(TraceFilterMode.Union, null, null, new[] { true, true, true, true, true }).SetName("union without lists");
        yield return new TestCaseData(TraceFilterMode.Union, new[] { TestItem.AddressB }, new[] { TestItem.AddressD }, new[] { false, true, false, true, false }).SetName("union excludes reward without recipient match");
        yield return new TestCaseData(TraceFilterMode.Union, new[] { TestItem.AddressD, TestItem.AddressA }, new[] { TestItem.AddressD, TestItem.AddressB }, new[] { true, false, true, true, false }).SetName("union of lists with several addresses");
        // An empty list matches no address, so under union it adds no matches.
        yield return new TestCaseData(TraceFilterMode.Union, Array.Empty<Address>(), c, new[] { false, true, true, false, true }).SetName("union with an empty sender list");
        yield return new TestCaseData(TraceFilterMode.Intersection, a, c, new[] { false, false, true, false, false }).SetName("intersection of both lists");
        yield return new TestCaseData(TraceFilterMode.Intersection, new[] { TestItem.AddressD, TestItem.AddressA }, new[] { TestItem.AddressD, TestItem.AddressB }, new[] { true, false, false, false, false }).SetName("intersection of lists with several addresses");
        yield return new TestCaseData(TraceFilterMode.Intersection, null, c, new[] { false, true, true, false, true }).SetName("intersection with recipient list only");
        yield return new TestCaseData(TraceFilterMode.Intersection, null, null, new[] { true, true, true, true, true }).SetName("intersection without lists");
    }

    [TestCaseSource(nameof(ModeCases))]
    public void Trace_filter_combines_address_lists_by_mode(TraceFilterMode mode, Address[]? from, Address[]? to, bool[] expected)
    {
        TxTraceFilter filter = new(from, to, 0, null, mode);
        bool[] actual = [.. new[] { AToB, BToC, AToC, BToB, RewardToC }.Select(filter.ShouldUseTxTrace)];
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Trace_filter_mode_round_trips_through_json([Values] TraceFilterMode mode)
    {
        string json = JsonSerializer.Serialize(mode, EthereumJsonSerializer.JsonOptions);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Is.EqualTo(mode == TraceFilterMode.Union ? "\"union\"" : "\"intersection\""));
            Assert.That(JsonSerializer.Deserialize<TraceFilterMode>(json, EthereumJsonSerializer.JsonOptions), Is.EqualTo(mode));
        }
    }

    [Test]
    public void Trace_filter_pages_union_matches()
    {
        TxTraceFilter filter = new([TestItem.AddressA], [TestItem.AddressC], 1, 2, TraceFilterMode.Union);
        bool[] actual = [.. new[] { AToB, BToB, BToC, RewardToC, AToC }.Select(filter.ShouldUseTxTrace)];
        Assert.That(actual, Is.EqualTo(new[] { false, false, true, true, false }));
    }

    [Test]
    public void Trace_filter_should_skip_expected_number_of_traces_()
    {
        TxTraceFilter traceFilter = new(new[] { TestItem.AddressA }, null, 2, 2, TraceFilterMode.Intersection);
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
}
