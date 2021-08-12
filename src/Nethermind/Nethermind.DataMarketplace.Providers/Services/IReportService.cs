using System.Threading.Tasks;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IReportService
    {
        Task<ConsumersReport> GetConsumersReportAsync(GetConsumersReport query);
        Task<PaymentClaimsReport> GetPaymentClaimsReportAsync(GetPaymentClaimsReport query);
    }
}