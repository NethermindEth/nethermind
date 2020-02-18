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
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories
{
    public class ConsumerDepositApprovalMongoRepository : IConsumerDepositApprovalRepository
    {
        private readonly IMongoDatabase _database;

        public ConsumerDepositApprovalMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<DepositApproval?> GetAsync(Keccak id)
            => DepositApprovals.Find(a => a.Id == id).FirstOrDefaultAsync()!;

        public async Task<PagedResult<DepositApproval>> BrowseAsync(GetConsumerDepositApprovals query)
        {
            if (query is null)
            {
                return PagedResult<DepositApproval>.Empty;
            }
            
            var depositApprovals = DepositApprovals.AsQueryable();
            if (!(query.DataAssetId is null))
            {
                depositApprovals = depositApprovals.Where(a => a.AssetId == query.DataAssetId);
            }

            if (!(query.Provider is null))
            {
                depositApprovals = depositApprovals.Where(a => a.Provider == query.Provider);
            }

            if (query.OnlyPending)
            {
                depositApprovals = depositApprovals.Where(a => a.State == DepositApprovalState.Pending);
            }

            return await depositApprovals.OrderByDescending(a => a.Timestamp).PaginateAsync(query);
        }

        public Task AddAsync(DepositApproval depositApproval)
            => DepositApprovals.InsertOneAsync(depositApproval);

        public Task UpdateAsync(DepositApproval depositApproval)
            => DepositApprovals.ReplaceOneAsync(a => a.Id == depositApproval.Id, depositApproval);

        private IMongoCollection<DepositApproval> DepositApprovals =>
            _database.GetCollection<DepositApproval>("consumerDepositApprovals");
    }
}