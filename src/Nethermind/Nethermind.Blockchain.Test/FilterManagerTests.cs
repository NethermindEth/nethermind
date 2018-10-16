using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        public void logs_should_not_be_empty_for_specific_topic()
            => LogsShouldNotBeEmpty(filter => filter.WithId(1)
                    .WithTopicExpressions(TestTopicExpressions.Specific(Keccak.Zero)),
                receiptContext => { });

        [Test]
        public void logs_should_not_be_empty_for_from_block_number_in_range()
            => LogsShouldNotBeEmpty(filter => filter
                    .FromBlock(UInt256.One),
                receiptContext => receiptContext.WithBlockNumber(new UInt256(2)));
        
        [Test]
        public void logs_should_be_empty_for_from_block_number_not_in_range()
            => LogsShouldBeEmpty(filter => filter
                    .FromBlock(UInt256.One),
                receiptContext => receiptContext.WithBlockNumber(UInt256.Zero));
        
        [Test]
        public void logs_should_not_be_empty_for_existing_address_and_specific_topic()
            => LogsShouldNotBeEmpty(filter => filter.WithId(1)
                    .FromBlock(UInt256.One)
                    .ToLatestBlock()
                    .WithAddress(Address.Zero)
                    .WithTopicExpressions(TestTopicExpressions.Specific(Keccak.Zero)),
                receiptContext => receiptContext.WithBlockNumber(UInt256.One));

        
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