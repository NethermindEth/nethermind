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

using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories
{
    public class ConsumerDepositApprovalInMemoryRepository : IConsumerDepositApprovalRepository
    {
        private readonly ConcurrentDictionary<Keccak, DepositApproval> _db =
            new ConcurrentDictionary<Keccak, DepositApproval>();

        public Task<DepositApproval?> GetAsync(Keccak id)
            => Task.FromResult(_db.TryGetValue(id, out var depositApproval) ? depositApproval : null);

        public Task<PagedResult<DepositApproval>> BrowseAsync(GetConsumerDepositApprovals query)
        {
            if (query is null)
            {
                return Task.FromResult(PagedResult<DepositApproval>.Empty);
            }
            
            var depositApprovals = _db.Values;
            if (!depositApprovals.Any())
            {
                return Task.FromResult(PagedResult<DepositApproval>.Empty);
            }

            var filteredDepositApprovals = depositApprovals.AsEnumerable();
            if (!(query.DataAssetId is null))
            {
                filteredDepositApprovals = filteredDepositApprovals.Where(a => a.AssetId == query.DataAssetId);
            }

            if (!(query.Provider is null))
            {
                filteredDepositApprovals = filteredDepositApprovals.Where(a => a.Provider == query.Provider);
            }

            if (query.OnlyPending)
            {
                filteredDepositApprovals = filteredDepositApprovals.Where(a => a.State == DepositApprovalState.Pending);
            }

            return Task.FromResult(filteredDepositApprovals.OrderByDescending(a => a.Timestamp).ToArray().Paginate(query));
        }
        
        public Task AddAsync(DepositApproval depositApproval)
        {
            _db.TryAdd(depositApproval.Id, depositApproval);

            return Task.CompletedTask;
        }

        public Task UpdateAsync(DepositApproval depositApproval) => Task.CompletedTask;
    }
}