// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;

namespace Nethermind.DataMarketplace.Providers.Repositories
{
    public interface IProviderSessionRepository
    {
        Task<ProviderSession> GetAsync(Keccak id);
        Task<ProviderSession?> GetPreviousAsync(ProviderSession session);
        Task<PagedResult<ProviderSession>> BrowseAsync(GetProviderSessions query);
        Task AddAsync(ProviderSession session);
        Task UpdateAsync(ProviderSession session);
    }
}
