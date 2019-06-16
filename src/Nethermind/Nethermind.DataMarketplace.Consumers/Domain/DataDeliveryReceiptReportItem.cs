using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Domain
{
    public class DataDeliveryReceiptReportItem
    {
        public Keccak Id { get; }
        public uint Number { get; }
        public Keccak SessionId { get; }
        public PublicKey NodeId { get; }
        public DataDeliveryReceiptRequest Request { get; }
        public DataDeliveryReceipt Receipt { get; }
        public ulong Timestamp { get; }
        public bool IsMerged { get; }
        public bool IsClaimed { get; }

        public DataDeliveryReceiptReportItem(Keccak id, uint number, Keccak sessionId, PublicKey nodeId,
            DataDeliveryReceiptRequest request, DataDeliveryReceipt receipt, ulong timestamp, bool isMerged,
            bool isClaimed)
        {
            Id = id;
            Number = number;
            SessionId = sessionId;
            NodeId = nodeId;
            Request = request;
            Receipt = receipt;
            Timestamp = timestamp;
            IsMerged = isMerged;
            IsClaimed = isClaimed;
        }
    }
}