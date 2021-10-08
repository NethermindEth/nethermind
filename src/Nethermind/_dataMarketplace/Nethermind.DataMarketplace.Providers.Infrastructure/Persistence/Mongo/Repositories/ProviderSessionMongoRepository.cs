using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories
{
    internal class ProviderSessionMongoRepository : IProviderSessionRepository
    {
        private readonly IMongoDatabase _database;

        public ProviderSessionMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<ProviderSession> GetAsync(Keccak id)
            => Sessions.Find(s => s.Id == id).FirstOrDefaultAsync();

        public async Task<ProviderSession?> GetPreviousAsync(ProviderSession session)
        {
            var previousSessions = await Filter(session.DepositId).Take(2).ToListAsync();
            switch (previousSessions.Count)
            {
                case 0:
                    return null;
                case 1:
                    return GetUniqueSession(session, previousSessions[0]);
                default:
                {
                    return GetUniqueSession(session, previousSessions[1]) ??
                           GetUniqueSession(session, previousSessions[0]);
                }
            }
        }

        private static ProviderSession? GetUniqueSession(ProviderSession current, ProviderSession previous)
            => current.Equals(previous) ? null : previous;

        public async Task<PagedResult<ProviderSession>> BrowseAsync(GetProviderSessions query)
            => await Filter(query.DepositId, query.DataAssetId, query.ConsumerNodeId, query.ConsumerAddress,
                query.ProviderNodeId, query.ProviderAddress).PaginateAsync(query);

        private IMongoQueryable<ProviderSession> Filter(Keccak? depositId = null, Keccak? dataAssetId = null,
            PublicKey? consumerNodeId = null, Address? consumerAddress = null, PublicKey? providerNodeId = null,
            Address? providerAddress = null)
        {
            var sessions = Sessions.AsQueryable();
            if (!(depositId is null))
            {
                sessions = sessions.Where(s => s.DepositId == depositId);
            }

            if (!(dataAssetId is null))
            {
                sessions = sessions.Where(s => s.DataAssetId == dataAssetId);
            }

            if (!(consumerNodeId is null))
            {
                sessions = sessions.Where(s => s.ConsumerNodeId == consumerNodeId);
            }

            if (!(consumerAddress is null))
            {
                sessions = sessions.Where(s => s.ConsumerAddress == consumerAddress);
            }

            if (!(providerNodeId is null))
            {
                sessions = sessions.Where(s => s.ProviderNodeId == providerNodeId);
            }

            if (!(providerAddress is null))
            {
                sessions = sessions.Where(s => s.ProviderAddress == providerAddress);
            }

            return sessions.OrderByDescending(s => s.StartTimestamp);
        }

        public Task AddAsync(ProviderSession session)
            => Sessions.InsertOneAsync(session);

        public Task UpdateAsync(ProviderSession session)
            => Sessions.ReplaceOneAsync(s => s.Id == session.Id, session);

        private IMongoCollection<ProviderSession> Sessions =>
            _database.GetCollection<ProviderSession>("providerSessions");
    }
}