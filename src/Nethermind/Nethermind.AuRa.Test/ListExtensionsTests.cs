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

        [Test]
        public void BinarySearchTest([Values(2, 10, 11, 20, 30, 19, 100)] int searchFor)
        {
            IList<int> iList = _list;
            Assert.That(iList.BinarySearch(searchFor, static (a, b) => a.CompareTo(b)), Is.EqualTo(_list.BinarySearch(searchFor)));
        }
    }
}
