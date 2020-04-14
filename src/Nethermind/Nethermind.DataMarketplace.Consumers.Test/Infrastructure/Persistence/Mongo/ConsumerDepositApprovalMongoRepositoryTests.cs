using System.Threading.Tasks;
using FluentAssertions;
using MongoDB.Driver;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence.Mongo
{
    [TestFixture]
    public class ConsumerDepositApprovalMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.TempDb.GetDatabase().DropCollection("consumerDepositApprovals");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerDepositApprovalMongoRepository(database);
            DepositApproval depositApproval = BuildDummyDepositApproval();
            await repo.AddAsync(depositApproval);
        }

        [Test]
        public async Task Can_get_by_id()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerDepositApprovalMongoRepository(database);
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
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerDepositApprovalMongoRepository(database);
            DepositApproval depositApproval = BuildDummyDepositApproval();
            await repo.AddAsync(depositApproval);
            await repo.BrowseAsync(new GetConsumerDepositApprovals());
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerDepositApprovalMongoRepository(database);
            DepositApproval depositApproval = BuildDummyDepositApproval();
            await repo.AddAsync(depositApproval);
            GetConsumerDepositApprovals query = new GetConsumerDepositApprovals();
            query.Provider = depositApproval.Provider;
            query.OnlyPending = true;
            query.DataAssetId = depositApproval.AssetId;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}