using System.Collections.Generic;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NSubstitute;
using NUnit.Framework;
using Build = Nethermind.Core.Test.Builders.Build;

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
        public void filter_logs_should_not_be_empty_for_zero_address_and_topic()
        {
            var filterId = 1;
            var filter = FilterBuilder.New()
                .WithId(filterId)
                .FromBlock(UInt256.Zero)
                .ToLatestBlock()
                .WithAddress(Address.Zero)
                .WithTopicExpressions(
                    TestTopicExpressions.Specific(Keccak.Zero))
                .Build();

            var transactionReceiptContext = TransactionReceiptContextBuilder.New()
                .WithBlockNumber(UInt256.One)
                .Build();

            _filterStore.GetAll().Returns(new List<Filter> {filter});

            _filterManager = new FilterManager(_filterStore);
            _filterManager.AddTransactionReceipt(transactionReceiptContext);
            var logs = _filterManager.GetLogs(filterId);

            Assert.IsNotEmpty(logs);
        }
    }
}