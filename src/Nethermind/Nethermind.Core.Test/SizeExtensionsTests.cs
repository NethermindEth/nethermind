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

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class SizeExtensionsTests
    {
        [TestCase(0)]
        [TestCase(1000)]
        [TestCase(9223372036)] // Int64.MaxValue / 1_000_000_000
        public void CheckOverflow_long(long testCase)
        {
            Assert.IsTrue(testCase.GB() >= 0);
        }

        [TestCase(0)]
        [TestCase(1000)]
        [TestCase(2147483647)] // Int32.MaxValue
        public void CheckOverflow_int(int testCase)
        {
            Assert.IsTrue(testCase.GB() >= 0);
        }
    }
}
