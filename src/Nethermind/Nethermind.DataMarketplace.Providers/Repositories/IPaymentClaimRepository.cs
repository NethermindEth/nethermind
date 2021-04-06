using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;

namespace Nethermind.DataMarketplace.Providers.Repositories
{
    public interface IPaymentClaimRepository
    {
        Task<PaymentClaim> GetAsync(Keccak id);
        Task<PagedResult<PaymentClaim>> BrowseAsync(GetPaymentClaims query);

        Task<PaymentsValueSummary> GetPaymentsSummary(
            Keccak? depositId = null,
            Keccak? assetId = null,
            Address? consumer = null);

        Task AddAsync(PaymentClaim paymentClaim);
        Task UpdateAsync(PaymentClaim paymentClaim);
    }
}