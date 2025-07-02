// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class CompactStackTests
{
    [Test]
    public void TestPush_then_Pop()
    {
        CompactStack<int> stack = new CompactStack<int>();
        for (int i = 0; i < 1024; i++)
        {
            stack.Push(i);
        }

        int expected = 1023;
        while (stack.TryPop(out int item))
        {
            item.Should().Be(expected);
            expected--;
        }
    }
}
