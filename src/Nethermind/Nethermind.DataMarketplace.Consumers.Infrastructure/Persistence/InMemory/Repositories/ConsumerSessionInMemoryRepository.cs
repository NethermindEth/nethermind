// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories
{
    public class ConsumerSessionInMemoryRepository : IConsumerSessionRepository
    {
        private readonly ConcurrentDictionary<Keccak, ConsumerSession> _db =
            new ConcurrentDictionary<Keccak, ConsumerSession>();

        public Task<ConsumerSession?> GetAsync(Keccak id)
            => Task.FromResult(_db.TryGetValue(id, out ConsumerSession? session) ? session : null);

        public Task<ConsumerSession?> GetPreviousAsync(ConsumerSession session)
        {
            var sessions = Filter(session.DepositId);
            switch (sessions.Count)
            {
                case 0:
                    return Task.FromResult<ConsumerSession?>(null);
                case 1:
                    return Task.FromResult<ConsumerSession?>(GetUniqueSession(session, sessions[0]));
                default:
                    {
                        var previousSessions = sessions.Take(2).ToArray();

                        return Task.FromResult<ConsumerSession?>(GetUniqueSession(session, previousSessions[1]) ??
                                                                 GetUniqueSession(session, previousSessions[0]));
                    }
            }
        }

        private static ConsumerSession? GetUniqueSession(ConsumerSession current, ConsumerSession previous)
            => current.Equals(previous) ? null : previous;

        public Task<PagedResult<ConsumerSession>> BrowseAsync(GetConsumerSessions query)
            => Task.FromResult(Filter(query.DepositId, query.DataAssetId, query.ConsumerNodeId, query.ConsumerAddress,
                query.ProviderNodeId, query.ProviderAddress).ToArray().Paginate(query));

        private IReadOnlyList<ConsumerSession> Filter(
            Keccak? depositId = null,
            Keccak? dataAssetId = null,
            PublicKey? consumerNodeId = null,
            Address? consumerAddress = null,
            PublicKey? providerNodeId = null,
            Address? providerAddress = null)
        {

            var sessions = _db.Values;
            if (!sessions.Any())
            {
                return Array.Empty<ConsumerSession>();
            }

            if (depositId is null && dataAssetId is null && consumerNodeId is null && consumerAddress is null
                && providerNodeId is null && providerAddress is null)
            {
                return sessions.ToArray();
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

        public Task AddAsync(ConsumerSession session)
        {
            _db.TryAdd(session.Id, session);

            return Task.CompletedTask;
        }

        public Task UpdateAsync(ConsumerSession session) => Task.CompletedTask;
    }
}
