//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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