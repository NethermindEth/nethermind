using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class FilterManagerTests
    {
        private IFilterStore _filterStore;
        private IFilterManager _filterManager;

        [SetUp]
        public void Setup()
        {
            _filterStore = Substitute.For<IFilterStore>();
        }

        [Test]
        public void logs_should_not_be_empty_for_default_filter_parameters()
            => LogsShouldNotBeEmpty(filter => { }, receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_earliest_type()
            => LogsShouldNotBeEmpty(filter => filter.FromEarliestBlock(), receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_pending_type()
            => LogsShouldNotBeEmpty(filter => filter.FromEarliestBlock(), receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_latest_type()
            => LogsShouldNotBeEmpty(filter => filter.FromEarliestBlock(), receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_to_block_earliest_type()
            => LogsShouldNotBeEmpty(filter => filter.ToEarliestBlock(), receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_to_block_pending_type()
            => LogsShouldNotBeEmpty(filter => filter.ToPendingBlock(), receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_to_block_latest_type()
            => LogsShouldNotBeEmpty(filter => filter.ToLatestBlock(), receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.FromBlock(UInt256.One),
                receiptContext => receiptContext.WithBlockNumber(new UInt256(2)));
        
        [Test]
        public void logs_should_be_empty_for_from_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(UInt256.One),
                receiptContext => receiptContext.WithBlockNumber(UInt256.Zero));

        [Test]
        public void logs_should_not_be_empty_for_to_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.ToBlock(new UInt256(2)),
                receiptContext => receiptContext.WithBlockNumber(UInt256.One));

        [Test]
        public void logs_should_be_empty_for_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.ToBlock(UInt256.One),
                receiptContext => receiptContext.WithBlockNumber(new UInt256(2)));

        [Test]
        public void logs_should_not_be_empty_for_from_block_number_in_range_and_to_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter.FromBlock(new UInt256(2)).ToBlock(new UInt256(6)),
                receiptContext => receiptContext.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_be_empty_for_from_block_number_in_range_and_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(new UInt256(2)).ToBlock(new UInt256(3)),
                receiptContext => receiptContext.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(new UInt256(5)).ToBlock(new UInt256(7)),
                receiptContext => receiptContext.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_be_empty_for_from_block_number_not_in_range_and_to_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter.FromBlock(new UInt256(2)).ToBlock(new UInt256(3)),
                receiptContext => receiptContext.WithBlockNumber(new UInt256(4)));

        [Test]
        public void logs_should_not_be_empty_for_existing_address()
            => LogsShouldNotBeEmpty(filter => filter.WithAddress(TestObject.AddressA),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithAddress(TestObject.AddressA).TestObject }).TestObject));

        [Test]
        public void logs_should_be_empty_for_non_existing_address()
            => LogsShouldBeEmpty(filter => filter.WithAddress(TestObject.AddressA),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithAddress(TestObject.AddressB).TestObject }).TestObject));

        [Test]
        public void logs_should_not_be_empty_for_existing_addresses()
            => LogsShouldNotBeEmpty(filter => filter.WithAddresses(new [] { TestObject.AddressA, TestObject.AddressB }),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithAddress(TestObject.AddressB).TestObject }).TestObject));

        [Test]
        public void logs_should_be_empty_for_non_existing_addresses()
            => LogsShouldBeEmpty(filter => filter.WithAddresses(new [] { TestObject.AddressA, TestObject.AddressB }),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithAddress(TestObject.AddressC).TestObject }).TestObject));

        [Test]
        public void logs_should_not_be_empty_for_existing_specific_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Specific(TestObject.KeccakA)),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithTopics(new [] { TestObject.KeccakA, TestObject.KeccakB }).TestObject }).TestObject));

        [Test]
        public void logs_should_be_empty_for_non_existing_specific_topic()
            => LogsShouldBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Specific(TestObject.KeccakA)),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithTopics(new [] { TestObject.KeccakB, TestObject.KeccakC }).TestObject }).TestObject));

        [Test]
        public void logs_should_not_be_empty_for_existing_any_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Any),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithTopics(new [] { TestObject.KeccakA, TestObject.KeccakB }).TestObject }).TestObject));

        [Test]
        public void logs_should_not_be_empty_for_existing_or_topic()
            => LogsShouldNotBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Or(new [] 
                        { 
                            TestTopicExpressions.Specific(TestObject.KeccakB),
                            TestTopicExpressions.Specific(TestObject.KeccakD)
                        })),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithTopics(new [] { TestObject.KeccakB, TestObject.KeccakC }).TestObject }).TestObject));

        [Test]
        public void logs_should_be_empty_for_non_existing_or_topic()
            => LogsShouldBeEmpty(filter => filter
                    .WithTopicExpressions(TestTopicExpressions.Or(new [] 
                        { 
                            TestTopicExpressions.Specific(TestObject.KeccakA),
                            TestTopicExpressions.Specific(TestObject.KeccakD)
                        })),
                receiptContext => receiptContext
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithTopics(new [] { TestObject.KeccakB, TestObject.KeccakC }).TestObject }).TestObject));

        [Test]
        public void logs_should_not_be_empty_for_existing_block_and_address_and_topics()
            => LogsShouldNotBeEmpty(filter => filter
                    .FromBlock(UInt256.One)
                    .ToBlock(new UInt256(10))
                    .WithAddress(TestObject.AddressA)
                    .WithTopicExpressions(TestTopicExpressions.Or(new [] 
                        { 
                            TestTopicExpressions.Specific(TestObject.KeccakB),
                            TestTopicExpressions.Specific(TestObject.KeccakD)
                        })),
                receiptContext => receiptContext
                    .WithBlockNumber(new UInt256(6))
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithAddress(TestObject.AddressA)
                            .WithTopics(new [] { TestObject.KeccakB, TestObject.KeccakC }).TestObject }).TestObject));

        [Test]
        public void logs_should_not_be_empty_for_existing_block_and_addresses_and_topics()
            => LogsShouldNotBeEmpty(filter => filter
                    .FromBlock(UInt256.One)
                    .ToBlock(new UInt256(10))
                    .WithAddresses(new [] { TestObject.AddressA, TestObject.AddressB })
                    .WithTopicExpressions(TestTopicExpressions.Or(new [] 
                        { 
                            TestTopicExpressions.Specific(TestObject.KeccakB),
                            TestTopicExpressions.Specific(TestObject.KeccakD)
                        })),
                receiptContext => receiptContext
                    .WithBlockNumber(new UInt256(6))
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithAddress(TestObject.AddressA)
                            .WithTopics(new [] { TestObject.KeccakB, TestObject.KeccakC }).TestObject }).TestObject));

        [Test]
        public void logs_should_be_empty_for_existing_block_and_addresses_and_non_existing_topic()
            => LogsShouldBeEmpty(filter => filter
                    .FromBlock(UInt256.One)
                    .ToBlock(new UInt256(10))
                    .WithAddresses(new [] { TestObject.AddressA, TestObject.AddressB })
                    .WithTopicExpressions(TestTopicExpressions.Or(new [] 
                        { 
                            TestTopicExpressions.Specific(TestObject.KeccakC),
                            TestTopicExpressions.Specific(TestObject.KeccakD)
                        })),
                receiptContext => receiptContext
                    .WithBlockNumber(new UInt256(6))
                    .WithReceipt(Build.A.TransactionReceipt
                        .WithLogs(new [] { Build.A.LogEntry.WithAddress(TestObject.AddressA)
                            .WithTopics(new [] { TestObject.KeccakB, TestObject.KeccakC }).TestObject }).TestObject));

        private void LogsShouldNotBeEmpty(Action<FilterBuilder> filterBuilder,
            Action<TransactionReceiptContextBuilder> receiptContextBuilder)
        {
            var filter = BuildFilter(filterBuilder);
            var receiptContext = BuildReceiptContext(receiptContextBuilder);

            Assert(filter, receiptContext, logs => logs.Should().NotBeEmpty());
        }
        
        private void LogsShouldBeEmpty(Action<FilterBuilder> filterBuilder,
            Action<TransactionReceiptContextBuilder> receiptContextBuilder)
        {
            var filter = BuildFilter(filterBuilder);
            var receiptContext = BuildReceiptContext(receiptContextBuilder);

            Assert(filter, receiptContext, logs => logs.Should().BeEmpty());
        }

        private void Assert(Filter filter, TransactionReceiptContext receiptContext,
            Action<IEnumerable<FilterLog>> logsAssertion)
        {
            _filterStore.GetAll().Returns(new List<Filter> {filter});
            _filterManager = new FilterManager(_filterStore);
            _filterManager.AddTransactionReceipt(receiptContext);

            var logs = _filterManager.GetLogs(filter.Id);

            logsAssertion(logs);
        }

        private static Filter BuildFilter(Action<FilterBuilder> builder)
        {
            var l = Build.A.TransactionReceipt;
            var builderInstance = FilterBuilder.New();
            builder(builderInstance);

            return builderInstance.Build();
        }

        private static TransactionReceiptContext BuildReceiptContext(Action<TransactionReceiptContextBuilder> builder)
        {
            var builderInstance = TransactionReceiptContextBuilder.New();
            builder(builderInstance);

            return builderInstance.Build();
        }
    }
}