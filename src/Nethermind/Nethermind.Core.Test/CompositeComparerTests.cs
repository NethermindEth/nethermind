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
using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using NUnit.Framework;


namespace Nethermind.Core.Test
{
    public class CompositeComparerTests
    {
        public static IEnumerable ComparersTestCases
        {
            get
            {
                TestCaseData BuildTest(IComparer<int>[] comparers, IComparer<int>[] expectedResult, string name) => 
                    new TestCaseData(new object[] {comparers}) {ExpectedResult = expectedResult, TestName = name};

                IComparer<int> a = Substitute.For<IComparer<int>>();
                IComparer<int> b = Substitute.For<IComparer<int>>();
                IComparer<int> c = Substitute.For<IComparer<int>>();
                IComparer<int> d = Substitute.For<IComparer<int>>();

                yield return BuildTest(new[] {a, b, c}, new[] {a, b, c}, "normal");
                yield return BuildTest(new[] {a.ThenBy(b), c}, new[] {a, b, c}, "ThenBy1->2/3");
                yield return BuildTest(new[] {a, b.ThenBy(c)}, new[] {a, b, c}, "ThenBy2->3/3");
                yield return BuildTest(new[] {a, b.ThenBy(c), d}, new[] {a, b, c, d}, "ThenBy2->3/4");
            }
        }
        
        [TestCaseSource(nameof(ComparersTestCases))]
        public IEnumerable<IComparer<int>> Composes_correctly(IEnumerable<IComparer<int>> comparers)
        {
            CompositeComparer<int> comparer = (CompositeComparer<int>) comparers.Aggregate((c1, c2) => c1.ThenBy(c2));
            return comparer._comparers;
        }
    }
}
