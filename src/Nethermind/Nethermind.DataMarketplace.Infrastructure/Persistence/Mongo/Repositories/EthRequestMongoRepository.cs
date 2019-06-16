using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories
{
    public class EthRequestMongoRepository : IEthRequestRepository
    {
        private readonly IMongoDatabase _database;

        public EthRequestMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<EthRequest> GetLatestAsync(string host)
            => EthRequests.AsQueryable().Where(r => r.Host == host).FirstOrDefaultAsync();

        public Task AddAsync(EthRequest request) => EthRequests.InsertOneAsync(request);
        public Task UpdateAsync(EthRequest request) => EthRequests.ReplaceOneAsync(r => r.Id == request.Id, request);

        private IMongoCollection<EthRequest> EthRequests => _database.GetCollection<EthRequest>("ethRequests");
    }
}