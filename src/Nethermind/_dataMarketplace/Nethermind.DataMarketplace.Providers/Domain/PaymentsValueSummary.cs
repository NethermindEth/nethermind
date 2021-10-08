
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class PaymentsValueSummary
    {
        public UInt256 Claimed { get; }
        public UInt256 Pending { get; }
        public UInt256 Income { get; }

        private PaymentsValueSummary()
        {
            Claimed = UInt256.Zero;
            Pending = UInt256.Zero;
            Income = UInt256.Zero;
        }

        public PaymentsValueSummary(UInt256 claimed, UInt256 pending, UInt256 income)
        {
            Claimed = claimed;
            Pending = pending;
            Income = income;
        }
        
        public static PaymentsValueSummary Empty => new PaymentsValueSummary();
    }
}