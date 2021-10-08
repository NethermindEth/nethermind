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
    public class ProviderSessionMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.Provider.GetDatabase().DropCollection("providerSessions");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderSessionMongoRepository(database);
            ProviderSession session = BuildDummySession();
            await repo.AddAsync(session);
        }
        
        [Test]
        public async Task Can_update_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderSessionMongoRepository(database);
            ProviderSession session = BuildDummySession();
            await repo.AddAsync(session);
            session.Start(1);
            await repo.UpdateAsync(session);
            var result = await repo.GetAsync(session.Id);
            result.Should().BeEquivalentTo(session);
            
        }

        private static ProviderSession BuildDummySession()
        {
            return BuildDummySession(TestItem.KeccakA);
        }

        private static ProviderSession BuildDummySession(Keccak id)
        {
            ProviderSession session = new ProviderSession(
                id,
                TestItem.KeccakB,
                TestItem.KeccakC,
                TestItem.AddressA,
                TestItem.PublicKeyA,
                TestItem.AddressB,
                TestItem.PublicKeyB,
                SessionState.ConsumerDisconnected,
                1,
                2,
                3,
                4,
                5,
                6,
                7,
                8,
                9,
                DataAvailability.SubscriptionEnded);
            return session;
        }

        [Test]
        public async Task Can_get_by_id()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderSessionMongoRepository(database);
            ProviderSession session = BuildDummySession();
            await repo.AddAsync(session);
            var result = await repo.GetAsync(session.Id);
            result.Should().BeEquivalentTo(session);
        }

        [Test]
        public async Task Can_get_previous_returns_null_if_only_same()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderSessionMongoRepository(database);
            ProviderSession session = BuildDummySession();
            await repo.AddAsync(session);
            var result = await repo.GetPreviousAsync(session);
            result.Should().BeNull();
        }

        [Test]
        public async Task Can_get_previous()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderSessionMongoRepository(database);
            ProviderSession session = BuildDummySession();
            await repo.AddAsync(session);
            ProviderSession session2 = BuildDummySession(Keccak.Zero);
            var result = await repo.GetPreviousAsync(session2);
            result.Should().BeEquivalentTo(session);
        }

        [Test]
        public async Task Can_browse()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderSessionMongoRepository(database);
            ProviderSession session = BuildDummySession();
            await repo.AddAsync(session);
            await repo.BrowseAsync(new GetProviderSessions());
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new ProviderSessionMongoRepository(database);
            ProviderSession session = BuildDummySession();
            await repo.AddAsync(session);
            var query = new GetProviderSessions();
            query.ConsumerAddress = session.ConsumerAddress;
            query.DepositId = session.DepositId;
            query.ProviderAddress = session.ProviderAddress;
            query.ConsumerNodeId = session.ConsumerNodeId;
            query.DataAssetId = session.DataAssetId;
            query.ProviderNodeId = session.ProviderNodeId;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}