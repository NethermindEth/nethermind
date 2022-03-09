//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
