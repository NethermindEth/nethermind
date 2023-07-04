// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Resettables;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[Parallelizable(ParallelScope.Self)]
public class ResettableListTests
{
    private static IEnumerable Tests
    {
        get
        {
            ResettableList<int> list = new();
            yield return new TestCaseData(list, 200) { ExpectedResult = 256 };
            yield return new TestCaseData(list, 0) { ExpectedResult = 128 };
            yield return new TestCaseData(list, 10) { ExpectedResult = 64 };
        }
    }

    [TestCaseSource(nameof(Tests))]
    public int Can_resize(ResettableList<int> list, int add)
    {
        list.AddRange(Enumerable.Range(0, add));
        list.Reset();
        list.Count.Should().Be(0);
        return list.Capacity;
    }
}
