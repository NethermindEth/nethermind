// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
