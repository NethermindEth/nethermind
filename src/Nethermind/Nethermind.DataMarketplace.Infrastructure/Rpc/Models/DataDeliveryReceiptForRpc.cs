using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataDeliveryReceiptForRpc
    {
        public string StatusCode { get; set; }
        public uint ConsumedUnits { get; set; }
        public uint UnpaidUnits { get; set; }

        public DataDeliveryReceiptForRpc()
        {
        }

        public DataDeliveryReceiptForRpc(DataDeliveryReceipt receipt)
        {
            StatusCode = receipt.StatusCode.ToString().ToLowerInvariant();
            ConsumedUnits = receipt.ConsumedUnits;
            UnpaidUnits = receipt.UnpaidUnits;
        }
    }
}