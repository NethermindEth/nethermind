// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
