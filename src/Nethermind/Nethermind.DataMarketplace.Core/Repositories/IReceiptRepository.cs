using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core.Repositories
{
    public interface IReceiptRepository
    {
        Task<DataDeliveryReceiptDetails> GetAsync(Keccak id);

        Task<IReadOnlyList<DataDeliveryReceiptDetails>> BrowseAsync(Keccak depositId = null, Keccak dataHeaderId = null,
            Keccak sessionId = null);

        Task AddAsync(DataDeliveryReceiptDetails receipt);
        Task UpdateAsync(DataDeliveryReceiptDetails receipt);
    }
}