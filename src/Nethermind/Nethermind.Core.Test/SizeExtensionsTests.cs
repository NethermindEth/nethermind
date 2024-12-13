// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class SizeExtensionsTests
{
    [TestCase(0)]
    [TestCase(1000)]
    [TestCase(9223372036)] // Int64.MaxValue / 1_000_000_000
    public void CheckOverflow_long(long testCase)
    {
        Assert.That(testCase.GB() >= 0, Is.True);
    }

    [TestCase(0)]
    [TestCase(1000)]
    [TestCase(2147483647)] // Int32.MaxValue
    public void CheckOverflow_int(int testCase)
    {
        Assert.That(testCase.GB() >= 0, Is.True);
    }
}
