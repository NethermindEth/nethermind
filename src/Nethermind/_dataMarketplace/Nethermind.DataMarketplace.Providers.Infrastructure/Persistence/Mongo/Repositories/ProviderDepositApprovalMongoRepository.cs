using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;

[assembly:InternalsVisibleTo("Nethermind.DataMarketplace.Providers.Infrastructure.Tests")]
namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories
{
    internal class ProviderDepositApprovalMongoRepository : IProviderDepositApprovalRepository
    {
        private readonly IMongoDatabase _database;

        public ProviderDepositApprovalMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<DepositApproval?> GetAsync(Keccak id)
            => DepositApprovals.Find<DepositApproval?>(a => a.Id == id).FirstOrDefaultAsync();

        public async Task<PagedResult<DepositApproval>> BrowseAsync(GetProviderDepositApprovals query)
        {
            if (query is null)
            {
                return PagedResult<DepositApproval>.Empty;
            }
            
            var depositApprovals = DepositApprovals.AsQueryable();
            if (!(query.DataAssetId is null))
            {
                depositApprovals = depositApprovals.Where(a => a.AssetId == query.DataAssetId);
            }

            if (!(query.Consumer is null))
            {
                depositApprovals = depositApprovals.Where(a => a.Consumer == query.Consumer);
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
            _database.GetCollection<DepositApproval>("providerDepositApprovals");
    }
}