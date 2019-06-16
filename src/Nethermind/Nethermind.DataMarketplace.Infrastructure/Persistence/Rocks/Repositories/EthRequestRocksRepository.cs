using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Store;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories
{
    public class EthRequestRocksRepository : IEthRequestRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<EthRequest> _rlpDecoder;

        public EthRequestRocksRepository(IDb database, IRlpDecoder<EthRequest> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }
        
        public Task<EthRequest> GetLatestAsync(string host)
        {
            var requestsBytes = _database.GetAll();
            if (requestsBytes.Length == 0)
            {
                return Task.FromResult<EthRequest>(null);
            }

            var requests = new EthRequest[requestsBytes.Length];
            for (var i = 0; i < requestsBytes.Length; i++)
            {
                requests[i] = Decode(requestsBytes[i]);
            }

            return Task.FromResult(requests.FirstOrDefault(r => r.Host == host));
        }

        public Task AddAsync(EthRequest request) => AddOrUpdateAsync(request);

        public Task UpdateAsync(EthRequest request) => AddOrUpdateAsync(request);
        
        private Task AddOrUpdateAsync(EthRequest request)
        {
            var rlp = _rlpDecoder.Encode(request);
            _database.Set(request.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private EthRequest Decode(byte[] bytes)
            => bytes is null
                ? null
                : _rlpDecoder.Decode(bytes.AsRlpContext());
    }
}