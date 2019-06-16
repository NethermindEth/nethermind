using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Domain
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