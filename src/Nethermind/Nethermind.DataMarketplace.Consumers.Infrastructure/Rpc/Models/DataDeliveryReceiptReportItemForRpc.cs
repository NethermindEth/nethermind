using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DataDeliveryReceiptReportItemForRpc
    {
        public Keccak Id { get; set; }
        public uint Number { get; set; }
        public Keccak SessionId { get; set; }
        public string NodeId { get; set; }
        public DataDeliveryReceiptRequestForRpc Request { get; set; }
        public DataDeliveryReceiptForRpc Receipt { get; set; }
        public ulong Timestamp { get; set; }
        public bool IsMerged { get; set; }
        public bool IsClaimed { get; set; }

        public DataDeliveryReceiptReportItemForRpc()
        {
        }

        public DataDeliveryReceiptReportItemForRpc(DataDeliveryReceiptReportItem receipt)
        {
            Id = receipt.Id;
            Number = receipt.Number;
            SessionId = receipt.SessionId;
            NodeId = receipt.NodeId.ToString();
            Request = new DataDeliveryReceiptRequestForRpc(receipt.Request);
            Receipt = new DataDeliveryReceiptForRpc(receipt.Receipt);
            Timestamp = receipt.Timestamp;
            IsMerged = receipt.IsMerged;
            IsClaimed = receipt.IsClaimed;
        }
    }
}