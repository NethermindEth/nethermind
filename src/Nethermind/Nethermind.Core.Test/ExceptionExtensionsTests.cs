// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
}
