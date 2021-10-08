using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IProviderTransactionsService
    {
        Task<IEnumerable<ResourceTransaction>> GetPendingAsync();
        Task<IEnumerable<ResourceTransaction>> GetAllTransactionsAsync();
        Task<UpdatedTransactionInfo> UpdatePaymentClaimGasPriceAsync(Keccak paymentClaimId, UInt256 gasPrice);
        Task<UpdatedTransactionInfo> CancelPaymentClaimAsync(Keccak paymentClaimId);
    }
}