/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories
{
    internal class ProviderSessionRocksRepository : IProviderSessionRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<ProviderSession> _rlpDecoder;
        private  IRlpStreamDecoder<ProviderSession> RlpStreamDecoder => (IRlpStreamDecoder<ProviderSession>)_rlpDecoder;
        private  IRlpObjectDecoder<ProviderSession> RlpObjectDecoder => (IRlpObjectDecoder<ProviderSession>)_rlpDecoder;

        public ProviderSessionRocksRepository(IDb database, IRlpNdmDecoder<ProviderSession> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder ?? throw new ArgumentNullException(nameof(rlpDecoder));
        }

        public Task<ProviderSession> GetAsync(Keccak id) => Task.FromResult(Decode(_database.Get(id)));

        public Task<ProviderSession?> GetPreviousAsync(ProviderSession session)
        {
            var sessions = Filter(session.DepositId);
            switch (sessions.Count)
            {
                case 0:
                    return Task.FromResult<ProviderSession?>(null);
                case 1:
                    return Task.FromResult<ProviderSession?>(GetUniqueSession(session, sessions[0]));
                default:
                {
                    var previousSessions = sessions.Take(2).ToArray();

                    return Task.FromResult<ProviderSession?>(GetUniqueSession(session, previousSessions[1]) ??
                                                             GetUniqueSession(session, previousSessions[0]));
                }
            }
        }

        private static ProviderSession? GetUniqueSession(ProviderSession current, ProviderSession previous)
            => current.Equals(previous) ? null : previous;

        public Task<PagedResult<ProviderSession>> BrowseAsync(GetProviderSessions query)
            => Task.FromResult(Filter(query.DepositId, query.DataAssetId, query.ConsumerNodeId, query.ConsumerAddress,
                query.ProviderNodeId, query.ProviderAddress).ToArray().Paginate(query));

        private IReadOnlyList<ProviderSession> Filter(Keccak? depositId = null, Keccak? dataAssetId = null,
            PublicKey? consumerNodeId = null, Address? consumerAddress = null, PublicKey? providerNodeId = null,
            Address? providerAddress = null)
        {
            var sessionsBytes = _database.GetAllValues().ToArray();
            if (sessionsBytes.Length == 0)
            {
                return Array.Empty<ProviderSession>();
            }

            var sessions = new ProviderSession[sessionsBytes.Length];
            for (var i = 0; i < sessionsBytes.Length; i++)
            {
                sessions[i] = Decode(sessionsBytes[i]);
            }

            if (depositId is null && dataAssetId is null && consumerNodeId is null && consumerAddress is null
                && providerNodeId is null && providerAddress is null)
            {
                return sessions;
            }

            var filteredSessions = sessions.AsEnumerable();
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

        public Task AddAsync(ProviderSession session) => AddOrUpdateAsync(session);

        public Task UpdateAsync(ProviderSession session) => AddOrUpdateAsync(session);

        private Task AddOrUpdateAsync(ProviderSession session)
        {
            var rlp = RlpObjectDecoder.Encode(session);
            _database.Set(session.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private ProviderSession Decode(byte[] bytes)
            => RlpStreamDecoder.Decode(bytes.AsRlpStream());
    }
}