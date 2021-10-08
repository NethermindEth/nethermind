using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class ReportService : IReportService
    {
        private readonly IConsumerRepository _consumerRepository;
        private readonly IPaymentClaimRepository _paymentClaimRepository;

        public ReportService(IConsumerRepository consumerRepository, IPaymentClaimRepository paymentClaimRepository)
        {
            _consumerRepository = consumerRepository;
            _paymentClaimRepository = paymentClaimRepository;
        }

        public async Task<ConsumersReport> GetConsumersReportAsync(GetConsumersReport query)
        {
            var consumers = await _consumerRepository.BrowseAsync(new GetConsumers
            {
                Results = int.MaxValue
            });
            if (!consumers.Items.Any())
            {
                return ConsumersReport.Empty;
            }

            var paymentClaims = await _paymentClaimRepository.BrowseAsync(new GetPaymentClaims
            {
                Page = 1,
                Results = int.MaxValue,
                DepositId = query.DepositId,
                AssetId = query.AssetId,
                Consumer = query.Consumer
            });

            var depositsPaymentClaimGroups = paymentClaims.Items.GroupBy(c => c.DepositId).Where(c => c.Any());
            var page = query.Page;
            if (page <= 0)
            {
                page = 1;
            }

            var results = query.Results;
            if (results <= 0)
            {
                results = 10;
            }

            var skip = (page - 1) * results;
            var items = (from depositsPaymentClaimsGroup in depositsPaymentClaimGroups.Skip(skip).Take(results)
                let consumer = consumers.Items.FirstOrDefault(c => c.DepositId == depositsPaymentClaimsGroup.Key)
                where !(consumer is null)
                let payments = CalculatePayments(depositsPaymentClaimsGroup)
                select new ConsumerReportItem(consumer.DataAsset.Id, consumer.DataAsset.Name,
                    consumer.DataRequest.Consumer, consumer.DepositId, consumer.DataRequest.Value, payments.Claimed,
                    payments.Pending, payments.Income)).ToList();

            var totalResults = items.Count();
            var totalPages = (int) Math.Ceiling((double) totalResults / query.Results);
            var paymentsSummary = await _paymentClaimRepository.GetPaymentsSummary(assetId: query.AssetId,
                consumer: query.Consumer);

            return new ConsumersReport(paymentsSummary.Claimed, paymentsSummary.Pending, paymentsSummary.Income,
                PagedResult<ConsumerReportItem>.Create(items, query.Page, query.Results, totalPages, totalResults));
        }

        private static PaymentsValueSummary CalculatePayments(IEnumerable<PaymentClaim> paymentClaims)
        {
            if (paymentClaims is null)
            {
                return PaymentsValueSummary.Empty;
            }

            var claimed = UInt256.Zero;
            var pending = UInt256.Zero;
            var income = UInt256.Zero;
            foreach (var paymentClaim in paymentClaims)
            {
                if (paymentClaim.Status == PaymentClaimStatus.Claimed)
                {
                    claimed += paymentClaim.ClaimedValue;
                    income += paymentClaim.Income;
                }
                else
                {
                    pending += paymentClaim.ClaimedValue;
                }
            }

            return new PaymentsValueSummary(claimed, pending, income);
        }

        public async Task<PaymentClaimsReport> GetPaymentClaimsReportAsync(GetPaymentClaimsReport query)
        {
            var paymentClaims = await _paymentClaimRepository.BrowseAsync(new GetPaymentClaims
            {
                DepositId = query.DepositId,
                AssetId = query.AssetId,
                Consumer = query.Consumer,
                Page = query.Page,
                Results = query.Results
            });

            if (paymentClaims.IsEmpty)
            {
                return PaymentClaimsReport.Empty;
            }

            var paymentsSummary = await _paymentClaimRepository.GetPaymentsSummary(query.DepositId, query.AssetId,
                query.Consumer);

            return new PaymentClaimsReport(paymentsSummary.Claimed, paymentsSummary.Pending,
                paymentsSummary.Income, paymentClaims);
        }
    }
}