using System.Linq;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class PaymentClaimsReportForRpc
    {
        public UInt256 TotalValue { get; set; }
        public UInt256 ClaimedValue { get; set; }
        public UInt256 PendingValue { get; set; }
        public UInt256 Income { get; set; }
        public UInt256 Cost { get; set; }
        public PagedResult<PaymentClaimForRpc> PaymentClaims { get; set; }

        public PaymentClaimsReportForRpc(PaymentClaimsReport report)
        {
            TotalValue = report.TotalValue;
            ClaimedValue = report.ClaimedValue;
            PendingValue = report.PendingValue;
            Income = report.Income;
            Cost = report.Cost;
            PaymentClaims = PagedResult<PaymentClaimForRpc>.From(report.PaymentClaims,
                report.PaymentClaims.Items.Select(c => new PaymentClaimForRpc(c)).ToArray());
        }
    }
}