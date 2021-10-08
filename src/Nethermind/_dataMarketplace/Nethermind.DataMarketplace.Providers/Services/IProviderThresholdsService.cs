using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IProviderThresholdsService
    {
        Task<UInt256> GetCurrentReceiptRequestAsync();
        Task<UInt256> GetCurrentReceiptsMergeAsync();
        Task<UInt256> GetCurrentPaymentClaimAsync();
        Task SetReceiptRequestAsync(UInt256 value);
        Task SetReceiptsMergeAsync(UInt256 value);
        Task SetPaymentClaimAsync(UInt256 value);
    }
}