// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Sessions.Repositories
{
    public interface IConsumerSessionRepository
    {
        Task<ConsumerSession?> GetAsync(Keccak id);
        Task<ConsumerSession?> GetPreviousAsync(ConsumerSession session);
        Task<PagedResult<ConsumerSession>> BrowseAsync(GetConsumerSessions query);
        Task AddAsync(ConsumerSession session);
        Task UpdateAsync(ConsumerSession session);
    }
}
