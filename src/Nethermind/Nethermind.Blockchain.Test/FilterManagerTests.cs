using System.Collections.Generic;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core.Test.Builders;
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
        public void filter_logs_should_not_be_empty()
        {
            var filterId = 1;
            var filter = FilterBuilder.CreateFilter()
                .WithId(filterId)
                .FromEarliestBlock()
                .ToLatestBlock()
                .WithAddress(TestObject.AddressA)
                .WithTopicExpressions(
                    TestTopicExpressions.Any, 
                    TestTopicExpressions.Specific(TestObject.KeccakA))
                .Build();

            _filterStore.GetAll().Returns(new List<Filter>{ filter});

            // var filterManager = new FilterManager(_filterStore);
            //TODO: Transaction receipt context stub
            // var logs = filterManager.GetLogs(filterId);

            // Assert.IsNotEmpty(logs);
        }
    }
}