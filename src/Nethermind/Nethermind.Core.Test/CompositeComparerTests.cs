// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                    new(new object[] { comparers }) { ExpectedResult = expectedResult, TestName = name };

                IComparer<int> a = Substitute.For<IComparer<int>>();
                IComparer<int> b = Substitute.For<IComparer<int>>();
                IComparer<int> c = Substitute.For<IComparer<int>>();
                IComparer<int> d = Substitute.For<IComparer<int>>();

                yield return BuildTest(new[] { a, b, c }, new[] { a, b, c }, "normal");
                yield return BuildTest(new[] { a.ThenBy(b), c }, new[] { a, b, c }, "ThenBy1->2/3");
                yield return BuildTest(new[] { a, b.ThenBy(c) }, new[] { a, b, c }, "ThenBy2->3/3");
                yield return BuildTest(new[] { a, b.ThenBy(c), d }, new[] { a, b, c, d }, "ThenBy2->3/4");
            }
        }

        [TestCaseSource(nameof(ComparersTestCases))]
        public IEnumerable<IComparer<int>> Composes_correctly(IEnumerable<IComparer<int>> comparers)
        {
            CompositeComparer<int> comparer = (CompositeComparer<int>)comparers.Aggregate((c1, c2) => c1.ThenBy(c2));
            return comparer._comparers;
        }
    }
}
