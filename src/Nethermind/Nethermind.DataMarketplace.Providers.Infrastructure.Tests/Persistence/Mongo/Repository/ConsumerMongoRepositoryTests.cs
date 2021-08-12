using System.Threading.Tasks;
using FluentAssertions;
using MongoDB.Driver;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Providers.Queries;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Tests.Persistence.Mongo.Repository
{
    [TestFixture]
    public class ConsumerMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.Provider.GetDatabase().DropCollection("consumers");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ConsumerMongoRepository(database);
            Consumer consumer = BuildDummyConsumer();
            await repo.AddAsync(consumer);
            Consumer result = await repo.GetAsync(consumer.DepositId);
            result.Should().BeEquivalentTo(consumer);
        }
        
        [Test]
        public async Task Can_update_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ConsumerMongoRepository(database);
            Consumer consumer = BuildDummyConsumer();
            await repo.AddAsync(consumer);
            consumer.SetConsumedUnits(100);
            await repo.UpdateAsync(consumer);
            Consumer result = await repo.GetAsync(consumer.DepositId);
            result.Should().BeEquivalentTo(consumer);
        }

        [Test]
        public async Task Can_get_by_id()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ConsumerMongoRepository(database);
            Consumer consumer = BuildDummyConsumer();
            await repo.AddAsync(consumer);
            Consumer result = await repo.GetAsync(consumer.DepositId);
            result.Should().BeEquivalentTo(consumer);
        }

        private static Consumer BuildDummyConsumer()
        {
            return new Consumer(TestItem.KeccakA, 1, new DataRequest(TestItem.KeccakB, 2, 3, 4, new byte[]{1,2,3}, TestItem.AddressA, TestItem.AddressB, new Signature(new byte[65])), BuildDummyDataAsset(), true);
        }
        
        private static DataAsset BuildDummyDataAsset()
        {
            return new DataAsset(TestItem.KeccakA, "a", "b", 1000, DataAssetUnitType.Unit, 100, 100000,
                new DataAssetRules(new DataAssetRule(123)), new DataAssetProvider(TestItem.AddressB, "c"));
        }

        [Test]
        public async Task Can_browse()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ConsumerMongoRepository(database);
            Consumer consumer = BuildDummyConsumer();
            await repo.AddAsync(consumer);
            await repo.BrowseAsync(new GetConsumers());
        }
        
        [Test]
        public async Task Can_browse_null_query()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ConsumerMongoRepository(database);
            Consumer consumer = BuildDummyConsumer();
            await repo.AddAsync(consumer);
            var result = await repo.BrowseAsync(null);
            result.Items.Should().HaveCount(0);
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ConsumerMongoRepository(database);
            Consumer consumer = BuildDummyConsumer();
            await repo.AddAsync(consumer);
            GetConsumers query = new GetConsumers();
            query.Address = consumer.DataRequest.Consumer;
            query.AssetId = consumer.DataAsset.Id;
            query.OnlyWithAvailableUnits = true;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}