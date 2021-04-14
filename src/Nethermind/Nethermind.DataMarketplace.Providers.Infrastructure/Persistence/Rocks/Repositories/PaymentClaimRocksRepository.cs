using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories
{
    internal class PaymentClaimRocksRepository : IPaymentClaimRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<PaymentClaim> _rlpDecoder;
        private  IRlpStreamDecoder<PaymentClaim> RlpStreamDecoder => (IRlpStreamDecoder<PaymentClaim>)_rlpDecoder;
        private  IRlpObjectDecoder<PaymentClaim> RlpObjectDecoder => (IRlpObjectDecoder<PaymentClaim>)_rlpDecoder;

        public PaymentClaimRocksRepository(IDb database, IRlpNdmDecoder<PaymentClaim> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder ?? throw new ArgumentNullException(nameof(rlpDecoder));
        }

        public async Task<PaymentClaim> GetAsync(Keccak id)
        {
            await Task.CompletedTask;

            return Decode(_database.Get(id));
        }

        public Task<PagedResult<PaymentClaim>> BrowseAsync(GetPaymentClaims query)
        {
            if (query is null)
            {
                return Task.FromResult(PagedResult<PaymentClaim>.Empty);
            }
            
            var paymentClaims = GetAll();
            if (paymentClaims.Length == 0)
            {
                return Task.FromResult(PagedResult<PaymentClaim>.Empty);
            }

            return Task.FromResult(Query(paymentClaims.AsEnumerable(), query.DepositId, query.AssetId,
                query.Consumer, query.OnlyUnclaimed, query.OnlyPending).ToArray().Paginate(query));
        }

        public Task<PaymentsValueSummary> GetPaymentsSummary(Keccak? depositId = null, Keccak? assetId = null,
            Address? consumer = null)
        {
            var paymentClaims = GetAll();
            if (paymentClaims.Length == 0)
            {
                return Task.FromResult(PaymentsValueSummary.Empty);
            }

            var values = Query(paymentClaims.AsEnumerable(), depositId, assetId, consumer)
                .Select(c => new {c.ClaimedValue, c.Income, c.Status});

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

            return Task.FromResult(new PaymentsValueSummary(claimed, pending, income));
        }

        private PaymentClaim[] GetAll()
        {
            var paymentClaimsBytes = _database.GetAllValues().ToArray();
            if (paymentClaimsBytes.Length == 0)
            {
                return Array.Empty<PaymentClaim>();
            }

            var paymentClaims = new PaymentClaim[paymentClaimsBytes.Length];
            for (var i = 0; i < paymentClaimsBytes.Length; i++)
            {
                paymentClaims[i] = Decode(paymentClaimsBytes[i]);
            }

            return paymentClaims;
        }

        private IEnumerable<PaymentClaim> Query(IEnumerable<PaymentClaim> paymentClaims, Keccak? depositId = null,
            Keccak? assetId = null, Address? consumer = null, bool onlyUnclaimed = false, bool onlyPending = false)
        {
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
                                                         c.Status != PaymentClaimStatus.ClaimedWithLoss &&
                                                         c.Status != PaymentClaimStatus.Rejected);
            }

            if (onlyPending)
            {
                paymentClaims = paymentClaims.Where(c => c.Transaction?.State == TransactionState.Pending
                                                         && c.Status == PaymentClaimStatus.Sent);
            }

            return paymentClaims.OrderByDescending(c => c.Timestamp);
        }

        public Task AddAsync(PaymentClaim paymentClaim) => AddOrUpdateAsync(paymentClaim);

        public Task UpdateAsync(PaymentClaim paymentClaim) => AddOrUpdateAsync(paymentClaim);

        private Task AddOrUpdateAsync(PaymentClaim paymentClaim)
        {
            var rlp = RlpObjectDecoder.Encode(paymentClaim);
            _database.Set(paymentClaim.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private PaymentClaim Decode(byte[] bytes)
            => RlpStreamDecoder.Decode(bytes.AsRlpStream());
    }
}