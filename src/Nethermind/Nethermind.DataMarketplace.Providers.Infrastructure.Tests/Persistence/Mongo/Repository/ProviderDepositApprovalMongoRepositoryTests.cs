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
    public class ProviderDepositApprovalMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.Provider.GetDatabase().DropCollection("providerDepositApprovals");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderDepositApprovalMongoRepository(database);
            DepositApproval depositApproval = BuildDummyDepositApproval();
            await repo.AddAsync(depositApproval);
        }

        [Test]
        public async Task Can_get_by_id()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderDepositApprovalMongoRepository(database);
            DepositApproval depositApproval = BuildDummyDepositApproval();
            await repo.AddAsync(depositApproval);
            DepositApproval result = await repo.GetAsync(depositApproval.Id);
            result.Should().BeEquivalentTo(depositApproval);
        }

        private static DepositApproval BuildDummyDepositApproval()
        {
            DepositApproval depositApproval = new DepositApproval(
                TestItem.KeccakB,
                "asset_name",
                "kyc", TestItem.AddressA,
                TestItem.AddressB,
                1,
                DepositApprovalState.Rejected);
            return depositApproval;
        }

        [Test]
        public async Task Can_browse()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderDepositApprovalMongoRepository(database);
            DepositApproval depositApproval = BuildDummyDepositApproval();
            await repo.AddAsync(depositApproval);
            await repo.BrowseAsync(new GetProviderDepositApprovals());
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderDepositApprovalMongoRepository(database);
            DepositApproval depositApproval = BuildDummyDepositApproval();
            await repo.AddAsync(depositApproval);
            GetProviderDepositApprovals query = new GetProviderDepositApprovals();
            query.Consumer = depositApproval.Consumer;
            query.OnlyPending = true;
            query.DataAssetId = depositApproval.AssetId;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}