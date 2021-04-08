using System.Numerics;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Policies
{
    public class ReceiptsPolicies : IReceiptsPolicies
    {
        private readonly IProviderThresholdsService _providerThresholdsService;

        public ReceiptsPolicies(IProviderThresholdsService providerThresholdsService)
        {
            _providerThresholdsService = providerThresholdsService;
        }

        public async Task<bool> CanRequestReceipts(long unpaidUnits, UInt256 unitPrice)
            => (UInt256) unpaidUnits * unitPrice >= await _providerThresholdsService.GetCurrentReceiptRequestAsync();
        
        public async Task<bool> CanMergeReceipts(long unmergedUnits, UInt256 unitPrice)
            => (UInt256) unmergedUnits * unitPrice >= await _providerThresholdsService.GetCurrentReceiptsMergeAsync();

        public async Task<bool> CanClaimPayment(long unclaimedUnits, UInt256 unitPrice)
            => (UInt256) unclaimedUnits * unitPrice >= await _providerThresholdsService.GetCurrentPaymentClaimAsync();
    }
}