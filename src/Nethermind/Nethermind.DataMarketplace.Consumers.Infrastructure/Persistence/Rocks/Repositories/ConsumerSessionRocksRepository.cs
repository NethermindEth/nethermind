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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories
{
    public class ConsumerSessionRocksRepository : IConsumerSessionRepository
    {
        private readonly IDb _database;
        private readonly IRlpNdmDecoder<ConsumerSession> _rlpDecoder;

        public ConsumerSessionRocksRepository(IDb database, IRlpNdmDecoder<ConsumerSession> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public Task<ConsumerSession?> GetAsync(Keccak id)
        {
            byte[] fromDatabase = _database.Get(id);
            return fromDatabase == null ? Task.FromResult<ConsumerSession?>(null) : Task.FromResult<ConsumerSession?>(Decode(fromDatabase));
        }

        public Task<ConsumerSession?> GetPreviousAsync(ConsumerSession session)
        {
            ConsumerSession[] sessions = Filter(session.DepositId);
            switch (sessions.Length)
            {
                case 0:
                    return Task.FromResult<ConsumerSession?>(null);
                case 1:
                    return Task.FromResult<ConsumerSession?>(GetUniqueSession(session, sessions[0]));
                default:
                {
                    ConsumerSession[] previousSessions = sessions.Take(2).ToArray();

                    return Task.FromResult<ConsumerSession?>(GetUniqueSession(session, previousSessions[1]) ??
                                                             GetUniqueSession(session, previousSessions[0]));
                }
            }
        }

        private static ConsumerSession? GetUniqueSession(ConsumerSession current, ConsumerSession previous)
            => current.Equals(previous) ? null : previous;


        public Task<PagedResult<ConsumerSession>> BrowseAsync(GetConsumerSessions query)
        {
            if (query is null)
            {
                throw new ArgumentNullException(nameof(query));
            }
            
            return Task.FromResult(Filter(query.DepositId, query.DataAssetId, query.ConsumerNodeId, query.ConsumerAddress,
                query.ProviderNodeId, query.ProviderAddress).Paginate(query));
        }

        private ConsumerSession[] Filter(
            Keccak? depositId = null,
            Keccak? dataAssetId = null,
            PublicKey? consumerNodeId = null,
            Address? consumerAddress = null,
            PublicKey? providerNodeId = null,
            Address? providerAddress = null)
        {
            byte[][] sessionsBytes = _database.GetAllValues().ToArray();
            if (sessionsBytes.Length == 0)
            {
                return Array.Empty<ConsumerSession>();
            }

            ConsumerSession[] sessions = new ConsumerSession[sessionsBytes.Length];
            for (int i = 0; i < sessionsBytes.Length; i++)
            {
                sessions[i] = Decode(sessionsBytes[i]);
            }

            if (depositId is null && dataAssetId is null && consumerNodeId is null && consumerAddress is null
                && providerNodeId is null && providerAddress is null)
            {
                return sessions;
            }

            IEnumerable<ConsumerSession> filteredSessions = sessions.AsEnumerable();
            if (!(depositId is null))
            {
                filteredSessions = filteredSessions.Where(s => s.DepositId == depositId);
            }

            if (!(dataAssetId is null))
            {
                filteredSessions = filteredSessions.Where(s => s.DataAssetId == dataAssetId);
            }

            if (!(consumerNodeId is null))
            {
                filteredSessions = filteredSessions.Where(s => s.ConsumerNodeId == consumerNodeId);
            }

            if (!(consumerAddress is null))
            {
                filteredSessions = filteredSessions.Where(s => s.ConsumerAddress == consumerAddress);
            }

            if (!(providerNodeId is null))
            {
                filteredSessions = filteredSessions.Where(s => s.ProviderNodeId == providerNodeId);
            }

            if (!(providerAddress is null))
            {
                filteredSessions = filteredSessions.Where(s => s.ProviderAddress == providerAddress);
            }

            return filteredSessions.OrderByDescending(s => s.StartTimestamp).ToArray();
        }

        public Task AddAsync(ConsumerSession session) => AddOrUpdateAsync(session);

        public Task UpdateAsync(ConsumerSession session) => AddOrUpdateAsync(session);

        private Task AddOrUpdateAsync(ConsumerSession session)
        {
            Serialization.Rlp.Rlp rlp = _rlpDecoder.Encode(session);
            _database.Set(session.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private ConsumerSession Decode(byte[] bytes)
            => _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}
