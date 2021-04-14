using System.Threading.Tasks;
using FluentAssertions;
using MongoDB.Driver;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Providers.Queries;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Tests.Persistence.Mongo.Repository
{
    [TestFixture]
    public class DataAssetMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.Provider.GetDatabase().DropCollection("dataAssets");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            DataAsset result = await repo.GetAsync(dataAsset.Id);
            result.Should().BeEquivalentTo(dataAsset);
        }
        
        [Test]
        public async Task Can_update_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            dataAsset.SetState(DataAssetState.Published);
            await repo.UpdateAsync(dataAsset);
            DataAsset result = await repo.GetAsync(dataAsset.Id);
            result.Should().BeEquivalentTo(dataAsset);
        }
        
        [Test]
        public async Task Can_check_if_exists()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            (await repo.ExistsAsync(dataAsset.Id)).Should().BeTrue();
        }
        
        [Test]
        public async Task Can_remove_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            await repo.RemoveAsync(dataAsset.Id);
            (await repo.ExistsAsync(dataAsset.Id)).Should().BeFalse();
            DataAsset result = await repo.GetAsync(dataAsset.Id);
            result.Should().BeNull();
        }

        [Test]
        public async Task Can_get_by_id()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            DataAsset result = await repo.GetAsync(dataAsset.Id);
            result.Should().BeEquivalentTo(dataAsset);
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
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            await repo.BrowseAsync(new GetDataAssets());
        }
        
        [Test]
        public async Task Can_browse_null_query()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            var result = await repo.BrowseAsync(null);
            result.Items.Should().HaveCount(0);
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new DataAssetMongoRepository(database);
            DataAsset dataAsset = BuildDummyDataAsset();
            await repo.AddAsync(dataAsset);
            GetDataAssets query = new GetDataAssets();
            query.OnlyPublishable = true;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}