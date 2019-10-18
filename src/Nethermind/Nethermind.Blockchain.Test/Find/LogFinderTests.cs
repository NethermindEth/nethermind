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

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Find
{
    public class LogFinderTests
    {
        private IBlockTree _blockTree;
        private IReceiptStorage _receiptStorage;
        private LogFinder _logFinder;

        [SetUp]
        public void SetUp()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).IsEip155Enabled.Returns(true);
            _receiptStorage = new InMemoryReceiptStorage();
            _blockTree = Build.A.BlockTree().WithTransactions(_receiptStorage, specProvider, LogsForBlockBuilder).OfChainLength(5).TestObject;
            
            _logFinder = new LogFinder(new BlockFinder(_blockTree),  _receiptStorage);

        }

        private IEnumerable<LogEntry> LogsForBlockBuilder(Block block, Transaction transaction)
        {
            if (block.Number == 1)
            {
                if (transaction.Value == 1)
                {
                    yield return Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).TestObject;
                }
                else if (transaction.Value == 2)
                {
                    yield return Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject;
                }
            }
            else if (block.Number == 4)
            {
                if (transaction.Value == 1)
                {
                    yield return Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject;
                }
                else if (transaction.Value == 2)
                {
                    yield return Build.A.LogEntry.WithAddress(TestItem.AddressC).WithTopics(TestItem.KeccakB, TestItem.KeccakA, TestItem.KeccakE).TestObject;
                    yield return Build.A.LogEntry.WithAddress(TestItem.AddressD).WithTopics(TestItem.KeccakD, TestItem.KeccakA).TestObject;
                }                
            }
        }

        [Test]
        public void filter_all_logs()
        {
            var logFilter = AllBlockFilter().Build();
            var logs = _logFinder.FindLogs(logFilter);
            logs.Length.Should().Be(5);
            logs.Select(l => (int) l.LogIndex).Should().BeEquivalentTo(new []{0, 1, 0, 1, 2});
        }
        
        [Test]
        public void filter_all_logs_when_receipts_ar_missing()
        {
            _receiptStorage = NullReceiptStorage.Instance;
            _logFinder = new LogFinder(new BlockFinder(_blockTree),  _receiptStorage);
            
            var logFilter = AllBlockFilter().Build();
            var logs = _logFinder.FindLogs(logFilter);
            logs.Length.Should().Be(0);
        }
        
        public static IEnumerable FilterByAddressTestsData
        {
            get
            {
                yield return new TestCaseData(new[] {TestItem.AddressA}, 2);
                yield return new TestCaseData(new[] {TestItem.AddressB}, 1);
                yield return new TestCaseData(new[] {TestItem.AddressC}, 1);
                yield return new TestCaseData(new[] {TestItem.AddressD}, 1);
                yield return new TestCaseData(new[] {TestItem.AddressA, TestItem.AddressC, TestItem.AddressD}, 4);
            }
        }

        [TestCaseSource(nameof(FilterByAddressTestsData))]
        public void filter_by_address(Address[] addresses, int expectedCount)
        {
            var filterBuilder = AllBlockFilter();
            filterBuilder = addresses.Length == 1 ? filterBuilder.WithAddress(addresses[0]) : filterBuilder.WithAddresses(addresses);
            var logFilter = filterBuilder.Build();
            
            var logs = _logFinder.FindLogs(logFilter);

            logs.Length.Should().Be(expectedCount);
        }

        public static IEnumerable FilterByTopicsTestsData
        {
            get
            {
                yield return new TestCaseData(new[] {TestTopicExpressions.Specific(TestItem.KeccakA)}, 3);
                yield return new TestCaseData(new[] {TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakB)}, 2);
                yield return new TestCaseData(new[] {TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Any}, 1);
                yield return new TestCaseData(new[] {TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Any, TestTopicExpressions.Specific(TestItem.KeccakE)}, 1);
                yield return new TestCaseData(new[] {TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB)}, 4);
                yield return new TestCaseData(new[] {TestTopicExpressions.Or(TestItem.KeccakA, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakB)}, 2);
            }
        }
            
        [TestCaseSource(nameof(FilterByTopicsTestsData))]
        public void filter_by_topics(TopicExpression[] topics, int expectedCount)
        {
            var logFilter = AllBlockFilter().WithTopicExpressions(topics).Build();
            
            var logs = _logFinder.FindLogs(logFilter);

            logs.Length.Should().Be(expectedCount);
        }
        
        public static IEnumerable FilterByBlocksTestsData
        {
            get
            {
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock().Build(), 3);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToLatestBlock().Build(), 5);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToPendingBlock().Build(), 5);
                yield return new TestCaseData(FilterBuilder.New().FromEarliestBlock().ToEarliestBlock().Build(), 0);
                yield return new TestCaseData(FilterBuilder.New().FromBlock(1).ToBlock(1).Build(), 2);
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToEarliestBlock().Build(), 0); //wrong order test
            }
        }
        
        [TestCaseSource(nameof(FilterByBlocksTestsData))]
        public void filter_by_blocks(LogFilter filter, int expectedCount)
        {
            var logs = _logFinder.FindLogs(filter);

            logs.Length.Should().Be(expectedCount);
        }
        
        public static IEnumerable ComplexFilterTestsData
        {
            get
            {
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                    .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                    .WithAddresses(TestItem.AddressC, TestItem.AddressD).Build(), 2);
                
                yield return new TestCaseData(FilterBuilder.New().FromLatestBlock().ToLatestBlock()
                    .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                    .WithAddresses(TestItem.AddressC).Build(), 1);
                    
                yield return new TestCaseData(FilterBuilder.New().FromFutureBlock().ToLatestBlock()
                    .WithTopicExpressions(TestTopicExpressions.Or(TestItem.KeccakD, TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA))
                    .WithAddresses(TestItem.AddressC).Build(), 0);
            }
        }
        
        [TestCaseSource(nameof(ComplexFilterTestsData))]
        public void complex_filter(LogFilter filter, int expectedCount)
        {
            var logs = _logFinder.FindLogs(filter);

            logs.Length.Should().Be(expectedCount);
        }

        private static FilterBuilder AllBlockFilter()
        {
            return FilterBuilder.New().FromEarliestBlock().ToPendingBlock();
        }
    }
}