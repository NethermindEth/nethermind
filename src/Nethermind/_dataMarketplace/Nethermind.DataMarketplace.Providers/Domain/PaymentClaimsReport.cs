using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class PaymentClaimsReport
    {
        public UInt256 TotalValue { get; }
        public UInt256 ClaimedValue { get; }
        public UInt256 PendingValue { get; }
        public UInt256 Income { get; }
        public UInt256 Cost { get; }
        public PagedResult<PaymentClaim> PaymentClaims { get; }

        private PaymentClaimsReport()
        {
            PaymentClaims = PagedResult<PaymentClaim>.Empty;
        }

        public PaymentClaimsReport(UInt256 claimedValue, UInt256 pendingValue, UInt256 income,
            PagedResult<PaymentClaim> paymentClaims)
        {
            TotalValue = claimedValue + pendingValue;
            ClaimedValue = claimedValue;
            PendingValue = pendingValue;
            Income = income;
            Cost = claimedValue - income;
            PaymentClaims = paymentClaims;
        }

        public static PaymentClaimsReport Empty => new PaymentClaimsReport();
    }
}