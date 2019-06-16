using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Queries;
using Nethermind.DataMarketplace.Consumers.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories
{
    public class DepositDetailsMongoRepository : IDepositDetailsRepository
    {
        private readonly IMongoDatabase _database;

        public DepositDetailsMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<DepositDetails> GetAsync(Keccak id)
            => Deposits.Find(c => c.Id == id).FirstOrDefaultAsync();

        public async Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query)
        {
            if (query is null)
            {
                return PagedResult<DepositDetails>.Empty;
            }

            var deposits = Deposits.AsQueryable();
            if (query.OnlyUnverified)
            {
                deposits = deposits.Where(d => d.VerificationTimestamp == 0);
            }

            return await deposits.OrderByDescending(d => d.Timestamp).PaginateAsync(query);
        }

        public Task AddAsync(DepositDetails deposit)
            => Deposits.InsertOneAsync(deposit);

        public Task UpdateAsync(DepositDetails deposit)
            => Deposits.ReplaceOneAsync(c => c.Id == deposit.Id, deposit);

        private IMongoCollection<DepositDetails> Deposits => _database.GetCollection<DepositDetails>("deposits");
    }
}