// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.AuRa;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class ListExtensionsTests
    {
        private readonly List<int> _list = Enumerable.Range(5, 10).Select(i => i * 2).ToList();

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
            iList.BinarySearch(searchFor, (a, b) => a.CompareTo(b)).Should().Be(_list.BinarySearch(searchFor));
        }
    }
}
