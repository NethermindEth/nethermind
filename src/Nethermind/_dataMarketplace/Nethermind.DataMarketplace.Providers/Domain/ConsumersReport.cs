using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class ConsumersReport
    {
        public UInt256 TotalValue { get; }
        public UInt256 ClaimedValue { get; }
        public UInt256 PendingValue { get; }
        public UInt256 Income { get; }
        public UInt256 Cost { get; }
        public PagedResult<ConsumerReportItem> Consumers { get; }

        private ConsumersReport()
        {
            Consumers = PagedResult<ConsumerReportItem>.Empty;
        }

        public ConsumersReport(UInt256 claimedValue, UInt256 pendingValue, UInt256 income,
            PagedResult<ConsumerReportItem> consumers)
        {
            TotalValue = claimedValue + pendingValue;
            ClaimedValue = claimedValue;
            PendingValue = pendingValue;
            Income = income;
            Cost = claimedValue - income;
            Consumers = consumers;
        }

        public static ConsumersReport Empty => new ConsumersReport();
    }
}