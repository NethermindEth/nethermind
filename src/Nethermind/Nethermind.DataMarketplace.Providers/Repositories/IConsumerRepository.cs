// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;

namespace Nethermind.DataMarketplace.Providers.Repositories
{
    public interface IConsumerRepository
    {
        Task<Consumer?> GetAsync(Keccak depositId);
        Task<PagedResult<Consumer>> BrowseAsync(GetConsumers query);
        Task AddAsync(Consumer consumer);
        Task UpdateAsync(Consumer consumer);
    }
}
