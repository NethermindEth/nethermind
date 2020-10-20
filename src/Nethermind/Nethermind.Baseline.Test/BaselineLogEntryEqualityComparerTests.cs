//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using Nethermind.Baseline.Tree;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public class BaselineLogEntryEqualityComparerTests
    {
        [Test]
        public void Equals_returns_expected_results([ValueSource(nameof(BaselineComparerTestCases))] BaselineComparerTest test)
        {
            Keccak[] leavesAndLeafTopics = new Keccak[]
           {
                new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922"),
                new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0")
           };
            var comparer = BaselineLogEntryEqualityComparer.Instance;

            var logEntry = new LogEntry(TestItem.Addresses[0], Array.Empty<byte>(), leavesAndLeafTopics);
            var actualResult = comparer.Equals(test.LogEntryToCompare, logEntry);
            Assert.AreEqual(test.ExpectedResult, actualResult);
        }

        public class BaselineComparerTest
        {
            public bool ExpectedResult { get; set; }

            public LogEntry LogEntryToCompare { get; set; }
        }

        public static IEnumerable<BaselineComparerTest> BaselineComparerTestCases
        {
            get
            {
                yield return new BaselineComparerTest()
                {
                    ExpectedResult = true,
                    LogEntryToCompare = new LogEntry(TestItem.Addresses[0], Array.Empty<byte>(), new Keccak[]
                    {
                        new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922"),
                        new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0")
                    })
                };
                yield return new BaselineComparerTest()
                {
                    ExpectedResult = false,
                    LogEntryToCompare = new LogEntry(TestItem.Addresses[1], Array.Empty<byte>(), new Keccak[]
                    {
                        new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922"),
                        new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0")
                    })
                };
                yield return new BaselineComparerTest()
                {
                    ExpectedResult = false,
                    LogEntryToCompare = null
                };
                yield return new BaselineComparerTest()
                {
                    ExpectedResult = true,
                    LogEntryToCompare = new LogEntry(TestItem.Addresses[0], Array.Empty<byte>(), new Keccak[]
                    {
                        new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922")
                    })
                };
                yield return new BaselineComparerTest()
                {
                    ExpectedResult = true,
                    LogEntryToCompare = new LogEntry(TestItem.Addresses[0], Array.Empty<byte>(), new Keccak[]
                    {
                        new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0")
                    })
                };
            }
        }
    }
}
