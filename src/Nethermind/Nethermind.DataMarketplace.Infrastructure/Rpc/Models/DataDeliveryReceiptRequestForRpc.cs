using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataDeliveryReceiptRequestForRpc
    {
        public uint Number { get; set; }
        public Keccak DepositId { get; set; }
        public UnitsRangeForRpc UnitsRange { get; set; }
        public bool IsSettlement { get; set; }

        public DataDeliveryReceiptRequestForRpc()
        {
        }

        public DataDeliveryReceiptRequestForRpc(DataDeliveryReceiptRequest request)
        {
            Number = request.Number;
            DepositId = request.DepositId;
            UnitsRange = new UnitsRangeForRpc(request.UnitsRange);
            IsSettlement = request.IsSettlement;
        }
    }
}