using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories
{
    internal class PaymentClaimMongoRepository : IPaymentClaimRepository
    {
        private readonly IMongoDatabase _database;

        public PaymentClaimMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<PaymentClaim> GetAsync(Keccak id)
            => PaymentClaims.Find(c => c.Id == id).SingleOrDefaultAsync();
        public async Task<PagedResult<PaymentClaim>> BrowseAsync(GetPaymentClaims query)
            => query is null
                ? PagedResult<PaymentClaim>.Empty
                : await Query(query.DepositId, query.AssetId, query.Consumer, query.OnlyUnclaimed, query.OnlyPending)
                    .PaginateAsync(query);

        public async Task<PaymentsValueSummary> GetPaymentsSummary(Keccak? depositId = null,
            Keccak? assetId = null, Address? consumer = null)
        {
            var values = await Query(depositId, assetId, consumer)
                .Select(c => new {c.ClaimedValue, c.Income, c.Status})
                .ToListAsync();

            if (values.Count == 0)
            {
                return PaymentsValueSummary.Empty;
            }

            var claimed = UInt256.Zero;
            var pending = UInt256.Zero;
            var income = UInt256.Zero;
            foreach (var value in values)
            {
                if (value.Status == PaymentClaimStatus.Claimed)
                {
                    claimed += value.ClaimedValue;
                    income += value.Income;
                }
                else
                {
                    pending += value.ClaimedValue;
                }
            }

            return new PaymentsValueSummary(claimed, pending, income);
        }

        private IMongoQueryable<PaymentClaim> Query(Keccak? depositId = null, Keccak? assetId = null,
            Address? consumer = null, bool onlyUnclaimed = false, bool onlyPending = false)
        {
            var paymentClaims = PaymentClaims.AsQueryable();
            if (!(depositId is null))
            {
                paymentClaims = paymentClaims.Where(c => c.DepositId == depositId);
            }

            if (!(assetId is null))
            {
                paymentClaims = paymentClaims.Where(c => c.AssetId == assetId);
            }

            if (!(consumer is null))
            {
                paymentClaims = paymentClaims.Where(c => c.Consumer == consumer);
            }

            if (onlyUnclaimed)
            {
                paymentClaims = paymentClaims.Where(c => c.Status != PaymentClaimStatus.Claimed &&
                                                         c.Status != PaymentClaimStatus.ClaimedWithLoss);
            }

            if (onlyPending)
            {
                paymentClaims = paymentClaims.Where(c => c.Transaction != null &&
                                                         c.Transaction.State == TransactionState.Pending
                                                         && c.Status == PaymentClaimStatus.Sent);
            }

            return paymentClaims.OrderByDescending(c => c.Timestamp);
        }

        public Task AddAsync(PaymentClaim paymentClaim)
            => PaymentClaims.InsertOneAsync(paymentClaim);

        public Task UpdateAsync(PaymentClaim paymentClaim)
            => PaymentClaims.ReplaceOneAsync(c => c.Id == paymentClaim.Id, paymentClaim);

        private IMongoCollection<PaymentClaim> PaymentClaims => _database.GetCollection<PaymentClaim>("paymentClaims");
    }
}