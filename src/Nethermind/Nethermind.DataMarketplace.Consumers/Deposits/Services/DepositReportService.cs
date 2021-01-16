//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositReportService : IDepositReportService
    {
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IReceiptRepository _receiptRepository;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;
        private readonly IDepositUnitsCalculator _depositUnitsCalculator;


        public DepositReportService(IDepositDetailsRepository depositRepository, IDepositUnitsCalculator depositUnitsCalculator, IReceiptRepository receiptRepository,
            IConsumerSessionRepository sessionRepository, ITimestamper timestamper)
        {
             _depositUnitsCalculator = depositUnitsCalculator;
            _depositRepository = depositRepository;
            _receiptRepository = receiptRepository;
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
        }

        private DepositReportItem ToReportItem(DepositDetails deposit, bool expired, uint consumedUnits, IEnumerable<DataDeliveryReceiptReportItem> receiptItems)
        {
            return new DepositReportItem(deposit.Id, deposit.DataAsset.Id, deposit.DataAsset.Name,
                deposit.DataAsset.Provider.Address, deposit.DataAsset.Provider.Name, deposit.Deposit.Value,
                deposit.Deposit.Units, deposit.Consumer, deposit.Timestamp, deposit.Deposit.ExpiryTime, expired,
                deposit.Transaction?.Hash, deposit.ConfirmationTimestamp, deposit.Confirmations,
                deposit.RequiredConfirmations, deposit.Confirmed, deposit.Rejected,
                deposit.ClaimedRefundTransaction?.Hash, deposit.RefundClaimed, consumedUnits, receiptItems);
        }

        public async Task<DepositsReport> GetAsync(GetDepositsReport query)
        {
            PagedResult<DepositDetails> deposits;
            if (query.DepositId == null)
            {
                deposits =
                    await _depositRepository.BrowseAsync(new GetDeposits
                    {
                        Results = int.MaxValue
                    });
            }
            else
            {
                DepositDetails? detailsOfOne = await _depositRepository.GetAsync(query.DepositId);
                if (detailsOfOne is null)
                {
                    return DepositsReport.Empty;    
                }
                
                deposits = PagedResult<DepositDetails>.Create(new[] {detailsOfOne},
                    1, 1, 1, 1);
            }

            if (deposits.Items.Count == 0)
            {
                return DepositsReport.Empty;
            }

            if (!deposits.Items.Any() || deposits.Items.Any(d => d is null))
            {
                return DepositsReport.Empty;
            }

            Dictionary<Keccak, DepositDetails> foundDeposits = deposits.Items
                .Where(d => (query.Provider is null || d.DataAsset.Provider.Address == query.Provider) &&
                            (query.AssetId is null || d.DataAsset.Id == query.AssetId))
                .ToDictionary(d => d.Id, d => d);
            if (!foundDeposits.Any())
            {
                return DepositsReport.Empty;
            }

            IEnumerable<Keccak> assetIds = foundDeposits.Select(d => d.Value.DataAsset.Id);
            IReadOnlyList<DataDeliveryReceiptDetails> receipts = await _receiptRepository.BrowseAsync(query.DepositId, query.AssetId);
            Dictionary<Keccak, IEnumerable<DataDeliveryReceiptDetails>> depositsReceipts = receipts.Where(r => assetIds.Contains(r.DataAssetId))
                .GroupBy(r => r.DepositId).ToDictionary(r => r.Key, r => r.AsEnumerable());

            int page = query.Page;
            if (page <= 0)
            {
                page = 1;
            }

            int results = query.Results;
            if (results <= 0)
            {
                results = 10;
            }

            uint timestamp = (uint) _timestamper.UnixTime.Seconds;
            int skip = (page - 1) * results;
            List<DepositReportItem> items = new List<DepositReportItem>();
            foreach ((Keccak _, DepositDetails deposit) in foundDeposits.OrderByDescending(d => d.Value.Timestamp).Skip(skip)
                .Take(results))
            {
                depositsReceipts.TryGetValue(deposit.Id, out IEnumerable<DataDeliveryReceiptDetails>? depositReceipts);
                bool expired = deposit.IsExpired(timestamp);
                IEnumerable<DataDeliveryReceiptReportItem>? receiptItems = depositReceipts?.Select(r => new DataDeliveryReceiptReportItem(r.Id, r.Number,
                    r.SessionId, r.ConsumerNodeId, r.Request, r.Receipt, r.Timestamp, r.IsMerged, r.IsClaimed));
                PagedResult<ConsumerSession> sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
                {
                    DepositId = deposit.Id,
                    Results = int.MaxValue
                });
                uint consumedUnits = await _depositUnitsCalculator.GetConsumedAsync(deposit);
                items.Add(ToReportItem(deposit, expired, consumedUnits, receiptItems ?? Enumerable.Empty<DataDeliveryReceiptReportItem>()));
            }

            (UInt256 total, UInt256 claimed, UInt256 refunded) = CalculateValues(foundDeposits, depositsReceipts);
            int totalResults = foundDeposits.Count;
            int totalPages = (int) Math.Ceiling((double) totalResults / query.Results);

            return new DepositsReport(total, claimed, refunded,
                PagedResult<DepositReportItem>.Create(items.OrderByDescending(i => i.Timestamp).ToList(),
                    query.Page, query.Results, totalPages, totalResults));
        }

        private static (UInt256 total, UInt256 claimed, UInt256 refunded) CalculateValues(
            IDictionary<Keccak, DepositDetails> deposits,
            IDictionary<Keccak, IEnumerable<DataDeliveryReceiptDetails>> depositsReceipts)
        {
            UInt256 total = UInt256.Zero;
            UInt256 claimed = UInt256.Zero;
            UInt256 refunded = UInt256.Zero;
            foreach ((Keccak _, DepositDetails deposit) in deposits)
            {
                UInt256 value = deposit.Deposit.Value;
                total += value;
                depositsReceipts.TryGetValue(deposit.Id, out IEnumerable<DataDeliveryReceiptDetails>? depositReceipts);
                if (depositReceipts is null)
                {
                    continue;
                }

                BigInteger unitPrice = (BigInteger) value / deposit.Deposit.Units;
                uint claimedUnits = 1 + depositReceipts.Max(r => r.Request.UnitsRange.To) -
                                    depositReceipts.Min(r => r.Request.UnitsRange.From);
                UInt256 claimedValue = (UInt256) (claimedUnits * unitPrice);
                UInt256 refundedValue = deposit.RefundClaimed ? value - claimedValue : 0;
                claimed += claimedValue;
                refunded += refundedValue;
            }

            return (total, claimed, refunded);
        }
    }
}
