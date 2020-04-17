using System.Threading.Tasks;
using MongoDB.Driver;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence.Mongo
{
    [TestFixture]
    public class ProviderMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.TempDb.GetDatabase().DropCollection("deposits");
        }

        [Test]
        public async Task Can_get_data_assets_from_deposits()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ProviderMongoRepository(database);
            await repo.GetDataAssetsAsync();
        }
       
        [Test]
        public async Task Can_get_providers_from_deposits()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ProviderMongoRepository(database);
            await repo.GetProvidersAsync();
        }
    }
}