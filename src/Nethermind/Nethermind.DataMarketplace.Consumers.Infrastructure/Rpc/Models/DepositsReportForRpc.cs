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

using System.Linq;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DepositsReportForRpc
    {
        public UInt256 TotalValue { get; }
        public UInt256 ClaimedValue { get; }
        public UInt256 RefundedValue { get; }
        public UInt256 RemainingValue { get; }
        public PagedResult<DepositReportItemForRpc> Deposits { get; }

        public DepositsReportForRpc(DepositsReport report)
        {
            TotalValue = report.TotalValue;
            ClaimedValue = report.ClaimedValue;
            RefundedValue = report.RefundedValue;
            RemainingValue = report.RemainingValue;
            Deposits = PagedResult<DepositReportItemForRpc>.From(report.Deposits,
                report.Deposits.Items.Select(d => new DepositReportItemForRpc(d)).ToArray());
        }
    }
}