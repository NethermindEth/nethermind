using System.Threading.Tasks;
using FluentAssertions;
using MongoDB.Driver;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Core.Domain;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence.Mongo
{
    [TestFixture]
    public class ConsumerSessionMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.TempDb.GetDatabase().DropCollection("consumerSessions");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerSessionMongoRepository(database);
            ConsumerSession session = BuildDummySession();
            await repo.AddAsync(session);
        }
        
        [Test]
        public async Task Can_update_async()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerSessionMongoRepository(database);
            ConsumerSession session = BuildDummySession();
            await repo.AddAsync(session);
            session.Start(1);
            await repo.UpdateAsync(session);
            var result = await repo.GetAsync(session.Id);
            result.Should().BeEquivalentTo(session);
            
        }

        private static ConsumerSession BuildDummySession()
        {
            return BuildDummySession(TestItem.KeccakA);
        }

        private static ConsumerSession BuildDummySession(Keccak id)
        {
            ConsumerSession session = new ConsumerSession(
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
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerSessionMongoRepository(database);
            ConsumerSession session = BuildDummySession();
            await repo.AddAsync(session);
            var result = await repo.GetAsync(session.Id);
            result.Should().BeEquivalentTo(session);
        }

        [Test]
        public async Task Can_get_previous_returns_null_if_only_same()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerSessionMongoRepository(database);
            ConsumerSession session = BuildDummySession();
            await repo.AddAsync(session);
            var result = await repo.GetPreviousAsync(session);
            result.Should().BeNull();
        }

        [Test]
        public async Task Can_get_previous()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerSessionMongoRepository(database);
            ConsumerSession session = BuildDummySession();
            await repo.AddAsync(session);
            ConsumerSession session2 = BuildDummySession(Keccak.Zero);
            var result = await repo.GetPreviousAsync(session2);
            result.Should().BeEquivalentTo(session);
        }

        [Test]
        public async Task Can_browse()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerSessionMongoRepository(database);
            ConsumerSession session = BuildDummySession();
            await repo.AddAsync(session);
            await repo.BrowseAsync(new GetConsumerSessions());
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.TempDb.GetDatabase();
            var repo = new ConsumerSessionMongoRepository(database);
            ConsumerSession session = BuildDummySession();
            await repo.AddAsync(session);
            var query = new GetConsumerSessions();
            query.ConsumerAddress = session.ConsumerAddress;
            query.DepositId = session.DepositId;
            query.ConsumerAddress = session.ConsumerAddress;
            query.ConsumerNodeId = session.ConsumerNodeId;
            query.DataAssetId = session.DataAssetId;
            query.ConsumerNodeId = session.ConsumerNodeId;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}