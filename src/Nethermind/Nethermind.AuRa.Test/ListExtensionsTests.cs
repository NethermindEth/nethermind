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
