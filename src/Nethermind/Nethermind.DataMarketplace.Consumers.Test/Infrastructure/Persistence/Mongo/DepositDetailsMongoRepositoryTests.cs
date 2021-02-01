using System;
using System.Threading.Tasks;
using FluentAssertions;
using MongoDB.Driver;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence.Mongo
{
    [TestFixture]
    public class DepositDetailsMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.TempDb.GetDatabase().DropCollection("deposits");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            var repo = new DepositDetailsMongoRepository(database, depositUnitsCalculator);
            DepositDetails depositDetails = BuildDummyDepositDetails();
            await repo.AddAsync(depositDetails);
        }

        [Test]
        public async Task Can_get_by_id()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            var repo = new DepositDetailsMongoRepository(database, depositUnitsCalculator);
            DepositDetails depositDetails = BuildDummyDepositDetails();
            await repo.AddAsync(depositDetails);
            DepositDetails result = await repo.GetAsync(depositDetails.Id);
            result.Should().BeEquivalentTo(depositDetails);
        }

        private static DepositDetails BuildDummyDepositDetails()
        {
            Deposit deposit = new Deposit(TestItem.KeccakA, 100, 100, 100);
            DataAssetProvider provider = new DataAssetProvider(TestItem.AddressA, "provider");
            DataAsset dataAsset = new DataAsset(TestItem.KeccakA, "data_asset", "desc", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1), null), provider, null, QueryType.Stream, DataAssetState.Published, null, false, null);
            DepositDetails details = new DepositDetails(
                deposit,
                dataAsset,
                TestItem.AddressA,
                Array.Empty<byte>(),
                10,
                Array.Empty<TransactionInfo>(),
                9,
                false,
                false,
                null,
                Array.Empty<TransactionInfo>(),
                false,
                false,
                null,
                0,
                6);

            return details;
        }

        [Test]
        public async Task Can_browse()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            var repo = new DepositDetailsMongoRepository(database, depositUnitsCalculator);
            DepositDetails depositDetails = BuildDummyDepositDetails();
            await repo.AddAsync(depositDetails);
            await repo.BrowseAsync(new GetDeposits());
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            IDepositUnitsCalculator depositUnitsCalculator = Substitute.For<IDepositUnitsCalculator>();
            var repo = new DepositDetailsMongoRepository(database, depositUnitsCalculator);
            DepositDetails depositDetails = BuildDummyDepositDetails();
            await repo.AddAsync(depositDetails);
            GetDeposits query = new GetDeposits();
            query.OnlyUnconfirmed = true;
            query.OnlyNotRejected = true;
            query.OnlyPending = true;
            query.CurrentBlockTimestamp = 1;
            query.EligibleToRefund = true;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}
