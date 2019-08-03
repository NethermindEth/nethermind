using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Queries;
using Nethermind.DataMarketplace.Consumers.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public class ReportService : IReportService
    {
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IReceiptRepository _receiptRepository;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;

        public ReportService(IDepositDetailsRepository depositRepository, IReceiptRepository receiptRepository,
            IConsumerSessionRepository sessionRepository, ITimestamper timestamper)
        {
            _depositRepository = depositRepository;
            _receiptRepository = receiptRepository;
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
        }

        public async Task<DepositsReport> GetDepositsReportAsync(GetDepositsReport query)
        {
            var deposits = query.DepositId is null
                ? await _depositRepository.BrowseAsync(new GetDeposits
                {
                    Results = int.MaxValue
                })
                : PagedResult<DepositDetails>.Create(new[] {await _depositRepository.GetAsync(query.DepositId)},
                    1, 1, 1, 1);
            if (!deposits.Items.Any() || deposits.Items.Any(d => d is null))
            {
                return DepositsReport.Empty;
            }

            var foundDeposits = deposits.Items
                .Where(d => (query.Provider is null || d.DataHeader.Provider.Address == query.Provider) &&
                            (query.HeaderId is null || d.DataHeader.Id == query.HeaderId))
                .ToDictionary(d => d.Id, d => d);
            if (!foundDeposits.Any())
            {
                return DepositsReport.Empty;
            }

            var headerIds = foundDeposits.Select(d => d.Value.DataHeader.Id);
            var receipts = await _receiptRepository.BrowseAsync(query.DepositId, query.HeaderId);
            var depositsReceipts = receipts.Where(r => headerIds.Contains(r.DataHeaderId))
                .GroupBy(r => r.DepositId).ToDictionary(r => r.Key, r => r.AsEnumerable());

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

            var now = _timestamper.EpochSeconds;
            var skip = (page - 1) * results;
            var items = new List<DepositReportItem>();
            foreach (var (_, deposit) in foundDeposits.OrderByDescending(d => d.Value.Timestamp).Skip(skip)
                .Take(results))
            {
                depositsReceipts.TryGetValue(deposit.Id, out var depositReceipts);
                var expired = now >= deposit.Deposit.ExpiryTime;
                var receiptItems = depositReceipts?.Select(r => new DataDeliveryReceiptReportItem(r.Id, r.Number,
                    r.SessionId, r.ConsumerNodeId, r.Request, r.Receipt, r.Timestamp, r.IsMerged, r.IsClaimed));
                var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
                {
                    DepositId = deposit.Id,
                    Results = int.MaxValue
                });
                var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
                items.Add(new DepositReportItem(deposit.Id, deposit.DataHeader.Id, deposit.DataHeader.Name,
                    deposit.DataHeader.Provider.Address, deposit.DataHeader.Provider.Name, deposit.Deposit.Value,
                    deposit.Deposit.Units, deposit.Timestamp, deposit.Deposit.ExpiryTime, expired,
                    deposit.TransactionHash, deposit.ConfirmationTimestamp, deposit.Confirmations,
                    deposit.RequiredConfirmations, deposit.Confirmed, deposit.ClaimedRefundTransactionHash,
                    consumedUnits, receiptItems));
            }

            var (total, claimed, refunded) = CalculateValues(foundDeposits, depositsReceipts);
            var totalResults = foundDeposits.Count;
            var totalPages = (int) Math.Ceiling((double) totalResults / query.Results);

            return new DepositsReport(total, claimed, refunded,
                PagedResult<DepositReportItem>.Create(items.OrderByDescending(i => i.Timestamp).ToList(),
                    query.Page, query.Results, totalPages, totalResults));
        }

        private static (UInt256 total, UInt256 claimed, UInt256 refunded) CalculateValues(
            IDictionary<Keccak, DepositDetails> deposits,
            IDictionary<Keccak, IEnumerable<DataDeliveryReceiptDetails>> depositsReceipts)
        {
            var total = UInt256.Zero;
            var claimed = UInt256.Zero;
            var refunded = UInt256.Zero;
            foreach (var (_, deposit) in deposits)
            {
                var value = deposit.Deposit.Value;
                total += value;
                depositsReceipts.TryGetValue(deposit.Id, out var depositReceipts);
                if (depositReceipts is null)
                {
                    continue;
                }

                var unitPrice = (BigInteger) value / deposit.Deposit.Units;
                var claimedUnits = 1 + depositReceipts.Max(r => r.Request.UnitsRange.To) -
                                   depositReceipts.Min(r => r.Request.UnitsRange.From);
                var claimedValue = (UInt256) (claimedUnits * unitPrice);
                var refundedValue = deposit.RefundClaimed ? value - claimedValue : 0;
                claimed += claimedValue;
                refunded += refundedValue;
            }

            return (total, claimed, refunded);
        }
    }
}