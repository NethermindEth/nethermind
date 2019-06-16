using System.Threading.Tasks;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Queries;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public interface IReportService
    {
        Task<DepositsReport> GetDepositsReportAsync(GetDepositsReport query);
    }
}