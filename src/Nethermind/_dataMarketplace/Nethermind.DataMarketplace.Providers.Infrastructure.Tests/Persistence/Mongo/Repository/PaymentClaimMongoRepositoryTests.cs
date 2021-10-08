using System.Linq;
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
    public class PaymentClaimMongoRepositoryTests
    {
        [TearDown]
        public void TearDown()
        {
            MongoForTest.Provider.GetDatabase().DropCollection("paymentClaims");
        }

        [Test]
        public async Task Can_add_async()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new PaymentClaimMongoRepository(database);
            PaymentClaim paymentClaim = BuildDummyPaymentClaim();
            await repo.AddAsync(paymentClaim);
        }

        [Test]
        public async Task Can_get_by_id()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new PaymentClaimMongoRepository(database);
            PaymentClaim paymentClaim = BuildDummyPaymentClaim();
            await repo.AddAsync(paymentClaim);
            PaymentClaim result = await repo.GetAsync(paymentClaim.Id);
            result.Should().BeEquivalentTo(paymentClaim);
        }

        private static PaymentClaim BuildDummyPaymentClaim()
        {
            PaymentClaim paymentClaim = new PaymentClaim(
                TestItem.KeccakA,
                TestItem.KeccakB,
                TestItem.KeccakC,
                "asset_name",
                1,
                2,
                new UnitsRange(1, 2),
                4,
                5,
                6,
                new byte[] {1, 2, 3},
                TestItem.AddressA,
                TestItem.AddressB,
                new Signature(new byte[65]),
                7,
                Enumerable.Empty<TransactionInfo>(),
                PaymentClaimStatus.Claimed);
            return paymentClaim;
        }

        [Test]
        public async Task Can_browse()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new PaymentClaimMongoRepository(database);
            PaymentClaim paymentClaim = BuildDummyPaymentClaim();
            await repo.AddAsync(paymentClaim);
            await repo.BrowseAsync(new GetPaymentClaims());
        }
        
        [Test]
        public async Task Can_get_payment_summary()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new PaymentClaimMongoRepository(database);
            PaymentClaim paymentClaim = BuildDummyPaymentClaim();
            await repo.AddAsync(paymentClaim);
            await repo.GetPaymentsSummary(null, null, null);
        }

        [Test]
        public async Task Can_browse_with_query_and_pagination()
        {
            IMongoDatabase database = MongoForTest.Provider.GetDatabase();
            var repo = new PaymentClaimMongoRepository(database);
            PaymentClaim paymentClaim = BuildDummyPaymentClaim();
            await repo.AddAsync(paymentClaim);
            GetPaymentClaims query = new GetPaymentClaims();
            query.Consumer = paymentClaim.Consumer;
            query.AssetId = paymentClaim.AssetId;
            query.DepositId = paymentClaim.DepositId;
            query.OnlyPending = false;
            query.OnlyUnclaimed = false;
            query.Page = 0;
            query.Results = 10;
            await repo.BrowseAsync(query);
        }
    }
}