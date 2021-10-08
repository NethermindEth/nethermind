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