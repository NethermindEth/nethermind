using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class ConsumerReportItem
    {
        public Keccak AssetId { get; private set; }
        public string AssetName { get; private set; }
        public Address Consumer { get; private set; }
        public Keccak DepositId { get; private set; }
        public UInt256 Value { get; private set; }
        public UInt256 ClaimedValue { get; private set; }
        public UInt256 PendingValue { get; private set; }
        public UInt256 Income { get; private set; }
        public UInt256 Cost { get; private set; }

        public ConsumerReportItem(Keccak assetId, string assetName, Address consumer, Keccak depositId,
            UInt256 value, UInt256 claimedValue, UInt256 pendingValue, UInt256 income)
        {
            AssetId = assetId;
            AssetName = assetName;
            Consumer = consumer;
            DepositId = depositId;
            Value = value;
            ClaimedValue = claimedValue;
            PendingValue = pendingValue;
            Income = income;
            Cost = claimedValue - income;
        }
    }
}