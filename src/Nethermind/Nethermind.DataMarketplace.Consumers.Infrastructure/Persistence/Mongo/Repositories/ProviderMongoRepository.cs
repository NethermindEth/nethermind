using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Repositories;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories
{
    public class ProviderMongoRepository : IProviderRepository
    {
        private readonly IMongoDatabase _database;

        public ProviderMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public async Task<IReadOnlyList<DataHeaderInfo>> GetDataHeadersAsync()
            => await Deposits.AsQueryable()
                .Select(d => new DataHeaderInfo(d.DataHeader.Id, d.DataHeader.Name, d.DataHeader.Description))
                .Distinct()
                .ToListAsync();

        public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync()
            => await Deposits.AsQueryable()
                .Select(d => new ProviderInfo(d.DataHeader.Provider.Name, d.DataHeader.Provider.Address))
                .Distinct()
                .ToListAsync();

        private IMongoCollection<DepositDetails> Deposits => _database.GetCollection<DepositDetails>("deposits");
    }
}