using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Queries;
using Nethermind.DataMarketplace.Consumers.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories
{
    public class ConsumerDepositApprovalMongoRepository : IConsumerDepositApprovalRepository
    {
        private readonly IMongoDatabase _database;

        public ConsumerDepositApprovalMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<DepositApproval> GetAsync(Keccak id)
            => DepositApprovals.Find(a => a.Id == id).FirstOrDefaultAsync();

        public async Task<PagedResult<DepositApproval>> BrowseAsync(GetConsumerDepositApprovals query)
        {
            if (query is null)
            {
                return PagedResult<DepositApproval>.Empty;
            }
            
            var depositApprovals = DepositApprovals.AsQueryable();
            if (!(query.DataHeaderId is null))
            {
                depositApprovals = depositApprovals.Where(a => a.HeaderId == query.DataHeaderId);
            }

            if (!(query.Provider is null))
            {
                depositApprovals = depositApprovals.Where(a => a.Provider == query.Provider);
            }

            if (query.OnlyPending)
            {
                depositApprovals = depositApprovals.Where(a => a.State == DepositApprovalState.Pending);
            }

            return await depositApprovals.OrderByDescending(a => a.Timestamp).PaginateAsync(query);
        }

        public Task AddAsync(DepositApproval depositApproval)
            => DepositApprovals.InsertOneAsync(depositApproval);

        public Task UpdateAsync(DepositApproval depositApproval)
            => DepositApprovals.ReplaceOneAsync(a => a.Id == depositApproval.Id, depositApproval);

        private IMongoCollection<DepositApproval> DepositApprovals =>
            _database.GetCollection<DepositApproval>("consumerDepositApprovals");
    }
}