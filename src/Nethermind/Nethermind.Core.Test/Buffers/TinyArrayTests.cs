// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Buffers;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers;

[TestFixture]
public class TinyArrayTests
{
    public static IEnumerable<TestCaseData> Payloads()
    {
        for (int len = 1; len <= 32; len++)
        {
            byte[] data = new byte[len];
            for (int i = 0; i < len; i++) data[i] = (byte)i;
            yield return new TestCaseData(data).SetName($"Length: {len:D2}");
        }
    }

    [Test]
    [TestCaseSource(nameof(Payloads))]
    public void Equality(byte[] src)
    {
        ISpanSource tiny = TinyArray.Create(src);

        tiny.Length.Should().Be(src.Length);

        tiny.SequenceEqual(src).Should().BeTrue();

        tiny.CommonPrefixLength(src).Should().Be(src.Length);
    }

    [Test]
    [TestCaseSource(nameof(Payloads))]
    public void Not_equal(byte[] src)
    {
        // Special case for 1, so that they differ
        var reversed = src.Length == 1 ? [(byte)(byte.MaxValue - src[0])] : src.Reverse().ToArray();

        ISpanSource tiny = TinyArray.Create(reversed);

        tiny.Length.Should().Be(src.Length);

        tiny.SequenceEqual(src).Should().BeFalse();

        tiny.CommonPrefixLength(src).Should().Be(0);
    }
}
