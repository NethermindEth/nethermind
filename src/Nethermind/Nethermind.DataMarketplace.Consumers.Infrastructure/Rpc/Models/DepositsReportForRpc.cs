using System.Linq;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DepositsReportForRpc
    {
        public UInt256 TotalValue { get; set; }
        public UInt256 ClaimedValue { get; set; }
        public UInt256 RefundedValue { get; set; }
        public UInt256 RemainingValue { get; set; }
        public PagedResult<DepositReportItemForRpc> Deposits { get; set; }

        public DepositsReportForRpc()
        {
        }

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