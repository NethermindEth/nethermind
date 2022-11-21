// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Domain
{
    public class DepositsReport
    {
        public UInt256 TotalValue { get; }
        public UInt256 ClaimedValue { get; }
        public UInt256 RefundedValue { get; }
        public UInt256 RemainingValue { get; }
        public PagedResult<DepositReportItem> Deposits { get; }

        private DepositsReport()
        {
            Deposits = PagedResult<DepositReportItem>.Empty;
        }

        public DepositsReport(UInt256 totalValue, UInt256 claimedValue, UInt256 refundedValue,
            PagedResult<DepositReportItem> deposits)
        {
            TotalValue = totalValue;
            ClaimedValue = claimedValue;
            RefundedValue = refundedValue;
            RemainingValue = totalValue - claimedValue - refundedValue;
            Deposits = deposits;
        }

        public static DepositsReport Empty => new DepositsReport();
    }
}
