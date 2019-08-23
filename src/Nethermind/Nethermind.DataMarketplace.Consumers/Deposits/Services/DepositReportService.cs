/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositReportService : IDepositReportService
    {
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IReceiptRepository _receiptRepository;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;

        public DepositReportService(IDepositDetailsRepository depositRepository, IReceiptRepository receiptRepository,
            IConsumerSessionRepository sessionRepository, ITimestamper timestamper)
        {
            _depositRepository = depositRepository;
            _receiptRepository = receiptRepository;
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
        }

        public async Task<DepositsReport> GetAsync(GetDepositsReport query)
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
                .Where(d => (query.Provider is null || d.DataAsset.Provider.Address == query.Provider) &&
                            (query.AssetId is null || d.DataAsset.Id == query.AssetId))
                .ToDictionary(d => d.Id, d => d);
            if (!foundDeposits.Any())
            {
                return DepositsReport.Empty;
            }

            var assetIds = foundDeposits.Select(d => d.Value.DataAsset.Id);
            var receipts = await _receiptRepository.BrowseAsync(query.DepositId, query.AssetId);
            var depositsReceipts = receipts.Where(r => assetIds.Contains(r.DataAssetId))
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

            var timestamp = (uint)_timestamper.EpochSeconds;
            var skip = (page - 1) * results;
            var items = new List<DepositReportItem>();
            foreach (var (_, deposit) in foundDeposits.OrderByDescending(d => d.Value.Timestamp).Skip(skip)
                .Take(results))
            {
                depositsReceipts.TryGetValue(deposit.Id, out var depositReceipts);
                var expired = deposit.IsExpired(timestamp);
                var receiptItems = depositReceipts?.Select(r => new DataDeliveryReceiptReportItem(r.Id, r.Number,
                    r.SessionId, r.ConsumerNodeId, r.Request, r.Receipt, r.Timestamp, r.IsMerged, r.IsClaimed));
                var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
                {
                    DepositId = deposit.Id,
                    Results = int.MaxValue
                });
                var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
                items.Add(new DepositReportItem(deposit.Id, deposit.DataAsset.Id, deposit.DataAsset.Name,
                    deposit.DataAsset.Provider.Address, deposit.DataAsset.Provider.Name, deposit.Deposit.Value,
                    deposit.Deposit.Units, deposit.Consumer, deposit.Timestamp, deposit.Deposit.ExpiryTime, expired,
                    deposit.TransactionHash, deposit.ConfirmationTimestamp, deposit.Confirmations,
                    deposit.RequiredConfirmations, deposit.Confirmed, deposit.Rejected,
                    deposit.ClaimedRefundTransactionHash, deposit.RefundClaimed, consumedUnits, receiptItems));
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