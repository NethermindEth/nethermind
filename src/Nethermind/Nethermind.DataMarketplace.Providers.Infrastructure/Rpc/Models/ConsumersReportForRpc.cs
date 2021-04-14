using System.Linq;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class ConsumersReportForRpc
    {
        public UInt256 TotalValue { get; set; }
        public UInt256 ClaimedValue { get; set; }
        public UInt256 PendingValue { get; set; }
        public UInt256 Income { get; set; }
        public UInt256 Cost { get; set; }
        public PagedResult<ConsumerReportItemForRpc> Consumers { get; }

        public ConsumersReportForRpc(ConsumersReport report)
        {
            TotalValue = report.TotalValue;
            ClaimedValue = report.ClaimedValue;
            PendingValue = report.PendingValue;
            Income = report.Income;
            Cost = report.Cost;
            Consumers = PagedResult<ConsumerReportItemForRpc>.From(report.Consumers,
                report.Consumers.Items.Select(c => new ConsumerReportItemForRpc(c)).ToArray());
        }
    }
}