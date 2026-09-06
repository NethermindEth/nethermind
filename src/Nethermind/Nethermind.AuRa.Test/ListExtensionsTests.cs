// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class ListExtensionsTests
    {
        private readonly List<int> _list = Enumerable.Range(5, 10).Select(static i => i * 2).ToList();

        [TestCase(2)]
        [TestCase(10)]
        [TestCase(11)]
        [TestCase(20)]
        [TestCase(30)]
        [TestCase(19)]
        [TestCase(100)]
        public void BinarySearchTest(int searchFor)
        {
            IList<int> iList = _list;
            Assert.That(iList.BinarySearch(searchFor, static (a, b) => a.CompareTo(b)), Is.EqualTo(_list.BinarySearch(searchFor)));
        }
    }
}
