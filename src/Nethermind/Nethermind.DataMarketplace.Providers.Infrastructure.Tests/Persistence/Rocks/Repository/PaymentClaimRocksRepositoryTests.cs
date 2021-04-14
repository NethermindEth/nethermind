using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories;
using Nethermind.DataMarketplace.Providers.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Tests.Persistence.Rocks.Repository
{
    [TestFixture]
    public class PaymentClaimRocksRepositoryTests
    {
        IPaymentClaimRepository repository;

        [SetUp]
        public void SetUp()
        {
            PaymentClaimDecoder.Init();
            UnitsRangeDecoder.Init();
            TransactionInfoDecoder.Init();

            var memDb = new MemDb();
            repository = new PaymentClaimRocksRepository(memDb, new PaymentClaimDecoder());
        }

        private IEnumerable<PaymentClaim> GetPaymentClaims()
        {
            yield return new PaymentClaim(
                id: TestItem.KeccakA,
                depositId: TestItem.KeccakB,
                assetId: TestItem.KeccakC,
                assetName: "asset1",
                units: 1,
                claimedUnits: 2,
                unitsRange: new UnitsRange(1, 2),
                value: 4,
                claimedValue: 5,
                expiryTime: 6,
                pepper: new byte[] {1, 2, 3},
                provider: TestItem.AddressA,
                consumer: TestItem.AddressB,
                signature: new Signature(1,2,37),
                timestamp: 7,
                transactions: Enumerable.Empty<TransactionInfo>(),
                status: PaymentClaimStatus.Claimed);

            yield return new PaymentClaim(
                id: TestItem.KeccakB,
                depositId: TestItem.KeccakA,
                assetId: TestItem.KeccakD,
                assetName: "asset2",
                units: 1,
                claimedUnits: 2,
                unitsRange: new UnitsRange(1, 2),
                value: 4,
                claimedValue: 5,
                expiryTime: 6,
                pepper: new byte[] {1, 2, 3},
                provider: TestItem.AddressA,
                consumer: TestItem.AddressC,
                signature: new Signature(1,2,37),
                timestamp: 7,
                transactions: Enumerable.Empty<TransactionInfo>(),
                status: PaymentClaimStatus.Cancelled);

            yield return new PaymentClaim(
                id: TestItem.KeccakC,
                depositId: TestItem.KeccakF,
                assetId: TestItem.KeccakE,
                assetName: "asset3",
                units: 1,
                claimedUnits: 2,
                unitsRange: new UnitsRange(1, 2),
                value: 4,
                claimedValue: 5,
                expiryTime: 6,
                pepper: new byte[] {1, 2, 3},
                provider: TestItem.AddressA,
                consumer: TestItem.AddressD,
                signature: new Signature(1,2,37),
                timestamp: 7,
                transactions: Enumerable.Empty<TransactionInfo>(),
                status: PaymentClaimStatus.Sent);

            yield return new PaymentClaim(
                id: TestItem.KeccakD,
                depositId: TestItem.KeccakE,
                assetId: TestItem.KeccakF,
                assetName: "asset4",
                units: 1,
                claimedUnits: 2,
                unitsRange: new UnitsRange(1, 2),
                value: 4,
                claimedValue: 5,
                expiryTime: 6,
                pepper: new byte[] {1, 2, 3},
                provider: TestItem.AddressA,
                consumer: TestItem.AddressA,
                signature: new Signature(1,2,37),
                timestamp: 7,
                transactions: new TransactionInfo[] { TransactionInfo.Default(Keccak.Zero, value: 10, gasPrice: 1, gasLimit: 100, timestamp: 10) },
                status: PaymentClaimStatus.Sent);
        }

        [Test]
        public async Task browse_will_return_pending_claims()
        {
            var claims = GetPaymentClaims();

            foreach(var claim in claims)
            {
                await repository.AddAsync(claim);
            }
            
            PagedResult<PaymentClaim> pendingClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                OnlyPending = true
            });

            Assert.IsTrue(pendingClaims.Items.Count == 1);
        }

        [Test]
        public async Task browse_will_return_unclaimed_claims()
        {
            var claims = GetPaymentClaims();

            foreach(var claim in claims)
            {
                await repository.AddAsync(claim);
            }
            
            PagedResult<PaymentClaim> unclaimedClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                OnlyUnclaimed = true
            });

            Assert.IsTrue(unclaimedClaims.Items.Count == 3);
        }

        [Test]
        public async Task can_browse_by_deposit_id()
        {
            var claims = GetPaymentClaims();

            foreach(var claim in claims)
            {
                await repository.AddAsync(claim);
            }

            var claim1 = claims.First(c => c.DepositId.Equals(TestItem.KeccakB));
            var claim2 = claims.First(c => c.DepositId.Equals(TestItem.KeccakF));
            
            PagedResult<PaymentClaim> browsedClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                DepositId = TestItem.KeccakB
            });

            Assert.IsTrue(browsedClaims.Items.Count == 1);

            var browsedClaim  = browsedClaims.Items[0]; 

            Assert.AreEqual(claim1.DepositId, browsedClaim.DepositId);
            Assert.AreEqual(claim1.Id, browsedClaim.Id);
            Assert.AreEqual(claim1.Value, browsedClaim.Value);
            Assert.AreEqual(claim1.Units, browsedClaim.Units);

            browsedClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                DepositId = TestItem.KeccakF
            });

            Assert.IsTrue(browsedClaims.Items.Count == 1);

            browsedClaim  = browsedClaims.Items[0]; 

            Assert.AreEqual(claim2.DepositId, browsedClaim.DepositId);
            Assert.AreEqual(claim2.Id, browsedClaim.Id);
            Assert.AreEqual(claim2.Value, browsedClaim.Value);
            Assert.AreEqual(claim2.Units, browsedClaim.Units);
        }

        [Test]
        public async Task can_browse_by_asset_id()
        {
            var claims = GetPaymentClaims();

            foreach(var claim in claims)
            {
                await repository.AddAsync(claim);
            }

            var claim1 = claims.First(c => c.AssetId.Equals(TestItem.KeccakD));
            var claim2 = claims.First(c => c.AssetId.Equals(TestItem.KeccakF));
            
            PagedResult<PaymentClaim> browsedClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                AssetId = TestItem.KeccakD
            });

            Assert.IsTrue(browsedClaims.Items.Count == 1);

            var browsedClaim  = browsedClaims.Items[0]; 

            Assert.AreEqual(claim1.DepositId, browsedClaim.DepositId);
            Assert.AreEqual(claim1.Id, browsedClaim.Id);
            Assert.AreEqual(claim1.Value, browsedClaim.Value);
            Assert.AreEqual(claim1.Units, browsedClaim.Units);

            browsedClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                AssetId = TestItem.KeccakF
            });

            Assert.IsTrue(browsedClaims.Items.Count == 1);

            browsedClaim  = browsedClaims.Items[0]; 

            Assert.AreEqual(claim2.DepositId, browsedClaim.DepositId);
            Assert.AreEqual(claim2.Id, browsedClaim.Id);
            Assert.AreEqual(claim2.Value, browsedClaim.Value);
            Assert.AreEqual(claim2.Units, browsedClaim.Units);
        } 

        [Test]
        public async Task can_browse_by_consumer_address()
        {
            var claims = GetPaymentClaims();

            foreach(var claim in claims)
            {
                await repository.AddAsync(claim);
            }

            var claim1 = claims.First(c => c.Consumer.Equals(TestItem.AddressA));
            var claim2 = claims.First(c => c.Consumer.Equals(TestItem.AddressC));
            
            PagedResult<PaymentClaim> browsedClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                Consumer = TestItem.AddressA
            });

            Assert.IsTrue(browsedClaims.Items.Count == 1);

            var browsedClaim  = browsedClaims.Items[0]; 

            Assert.AreEqual(claim1.DepositId, browsedClaim.DepositId);
            Assert.AreEqual(claim1.Id, browsedClaim.Id);
            Assert.AreEqual(claim1.Value, browsedClaim.Value);
            Assert.AreEqual(claim1.Units, browsedClaim.Units);

            browsedClaims = await repository.BrowseAsync(new GetPaymentClaims
            {
                Consumer = TestItem.AddressC
            });

            Assert.IsTrue(browsedClaims.Items.Count == 1);

            browsedClaim  = browsedClaims.Items[0]; 

            Assert.AreEqual(claim2.DepositId, browsedClaim.DepositId);
            Assert.AreEqual(claim2.Id, browsedClaim.Id);
            Assert.AreEqual(claim2.Value, browsedClaim.Value);
            Assert.AreEqual(claim2.Units, browsedClaim.Units);
        } 
    }
}