// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories
{
    public class ConsumerSessionMongoRepository : IConsumerSessionRepository
    {
        private readonly IMongoDatabase _database;

        public ConsumerSessionMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<ConsumerSession?> GetAsync(Keccak id)
            => Sessions.Find(s => s.Id == id).FirstOrDefaultAsync()!;

        public async Task<ConsumerSession?> GetPreviousAsync(ConsumerSession session)
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

        private static ConsumerSession? GetUniqueSession(ConsumerSession current, ConsumerSession previous)
            => current.Equals(previous) ? null : previous;

        public async Task<PagedResult<ConsumerSession>> BrowseAsync(GetConsumerSessions query)
            => await Filter(query.DepositId, query.DataAssetId, query.ConsumerNodeId, query.ConsumerAddress,
                query.ProviderNodeId, query.ProviderAddress).PaginateAsync(query);

        private IMongoQueryable<ConsumerSession> Filter(
            Keccak? depositId = null,
            Keccak? dataAssetId = null,
            PublicKey? consumerNodeId = null,
            Address? consumerAddress = null,
            PublicKey? providerNodeId = null,
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

        public Task AddAsync(ConsumerSession session)
            => Sessions.InsertOneAsync(session);

        public Task UpdateAsync(ConsumerSession session)
            => Sessions.ReplaceOneAsync(s => s.Id == session.Id, session);

        private IMongoCollection<ConsumerSession> Sessions =>
            _database.GetCollection<ConsumerSession>("consumerSessions");
    }
}
