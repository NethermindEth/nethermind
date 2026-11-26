// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ExceptionExtensionsTests
{
    private static readonly Exception Chain =
        new InvalidOperationException("outer", new ArgumentException("middle", new FormatException("inner")));

    [TestCase(1, "System.InvalidOperationException: outer ---> ...")]
    [TestCase(2, "System.InvalidOperationException: outer ---> System.ArgumentException: middle ---> ...")]
    [TestCase(3, "System.InvalidOperationException: outer ---> System.ArgumentException: middle ---> System.FormatException: inner")]
    [TestCase(4, "System.InvalidOperationException: outer ---> System.ArgumentException: middle ---> System.FormatException: inner")]
    public void DescribeChain_lists_at_most_max_depth_exceptions(int maxDepth, string expected) =>
        Assert.That(Chain.DescribeChain(maxDepth), Is.EqualTo(expected));

    [Test]
    public void DescribeChain_rejects_a_max_depth_below_one([Range(-1, 0)] int maxDepth) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Chain.DescribeChain(maxDepth));

    private static IEnumerable<TestCaseData> IsOrWrapsCases()
    {
        yield return new TestCaseData(new OutOfMemoryException()).Returns(true).SetArgDisplayNames("itself");
        yield return new TestCaseData(new InsufficientMemoryException()).Returns(true).SetArgDisplayNames("derived type");
        yield return new TestCaseData(new FormatException("outer", new OutOfMemoryException())).Returns(true).SetArgDisplayNames("inner");
        yield return new TestCaseData(new FormatException("outer", new TypeInitializationException("T", new OutOfMemoryException())))
            .Returns(true).SetArgDisplayNames("inner of inner");
        yield return new TestCaseData(new AggregateException(new FormatException(), new OutOfMemoryException()))
            .Returns(true).SetArgDisplayNames("second of aggregate");
        yield return new TestCaseData(new FormatException("outer", new AggregateException(new FormatException(), new FormatException("inner", new OutOfMemoryException()))))
            .Returns(true).SetArgDisplayNames("inner of aggregate's second");
        yield return new TestCaseData(Chain).Returns(false).SetArgDisplayNames("none in chain");
        yield return new TestCaseData(new AggregateException(new FormatException(), new ArgumentException())).Returns(false).SetArgDisplayNames("none in aggregate");
    }

    [TestCaseSource(nameof(IsOrWrapsCases))]
    public bool IsOrWraps_finds_the_type_at_any_depth(Exception exception) => exception.IsOrWraps<OutOfMemoryException>();
}
