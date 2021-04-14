using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class ConsumerReportItemForRpc
    {
        public Keccak AssetId { get; set; }
        public string AssetName { get; set; }
        public Address Consumer { get; set; }
        public Keccak DepositId { get; set; }
        public UInt256 Value { get; set; }
        public UInt256 ClaimedValue { get; set; }
        public UInt256 PendingValue { get; set; }
        public UInt256 Income { get; set; }
        public UInt256 Cost { get; set; }

        public ConsumerReportItemForRpc(ConsumerReportItem item)
        {
            AssetId = item.AssetId;
            AssetName = item.AssetName;
            Consumer = item.Consumer;
            DepositId = item.DepositId;
            Value = item.Value;
            ClaimedValue = item.ClaimedValue;
            PendingValue = item.PendingValue;
            Cost = item.Cost;
            Income = item.Income;
        }
    }
}