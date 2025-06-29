// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class MathTests
{
    private static IEnumerable<TestCaseData> PadTo32TestCases()
    {
        yield return new TestCaseData(0, 0);
        yield return new TestCaseData(1, 32);
        yield return new TestCaseData(31, 32);
        yield return new TestCaseData(32, 32);
        yield return new TestCaseData(33, 64);
        yield return new TestCaseData(63, 64);
        yield return new TestCaseData(64, 64);
        yield return new TestCaseData(65, 96);
        yield return new TestCaseData(95, 96);
        yield return new TestCaseData(96, 96);
        yield return new TestCaseData(97, 128);
        yield return new TestCaseData(127, 128);
        yield return new TestCaseData(128, 128);
        yield return new TestCaseData(129, 160);
        yield return new TestCaseData(1024, 1024);
        yield return new TestCaseData(1025, 1056);
        yield return new TestCaseData(int.MaxValue - 33, int.MaxValue - 31);
        yield return new TestCaseData(int.MaxValue - 32, int.MaxValue - 31);
        yield return new TestCaseData(int.MaxValue - 31, int.MaxValue - 31);
    }

    [TestCaseSource(nameof(PadTo32TestCases))]
    public void PadTo32(int value, int expected)
    {
        var actual = Math.PadTo32(value);
        actual.Should().Be(expected);
    }
}
