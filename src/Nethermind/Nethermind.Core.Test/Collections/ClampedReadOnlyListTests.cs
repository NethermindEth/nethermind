// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class ClampedReadOnlyListTests
{
    [Test]
    public void When_ListIsSmallerThanClamp_ReturnTheSameObject()
    {
        int[] baseList = Enumerable.Repeat(0, 100).ToArray();
        IReadOnlyList<int> clamped = baseList.Clamp(1000);
        clamped.Should().BeSameAs(baseList);
    }

    [Test]
    public void When_Clamped_ReturnOnlyPrefixOfOriginalArray()
    {
        int[] baseList = Enumerable.Range(0, 100).ToArray();
        IReadOnlyList<int> clamped = baseList.Clamp(10);

        clamped.Should().BeEquivalentTo(Enumerable.Range(0, 10).ToArray());
    }

    [Test]
    public void When_Clamped_AttemptToIndexAboveRange_WillThrow()
    {
        int[] baseList = Enumerable.Range(0, 100).ToArray();
        IReadOnlyList<int> clamped = baseList.Clamp(10);

        clamped.Count.Should().Be(10);

        Action act = () =>
        {
            int _ = clamped[11];
        };

        act.Should().Throw<IndexOutOfRangeException>();
    }
}
