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
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories
{
    public class ConsumerDepositApprovalRocksRepository : IConsumerDepositApprovalRepository
    {
        private readonly IDb _database;
        private readonly IRlpNdmDecoder<DepositApproval> _rlpDecoder;

        public ConsumerDepositApprovalRocksRepository(IDb database, IRlpNdmDecoder<DepositApproval> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public Task<DepositApproval?> GetAsync(Keccak id)
        {
            byte[]? fromDatabase = _database.Get(id);
            return fromDatabase == null ? Task.FromResult<DepositApproval?>(null) : Task.FromResult<DepositApproval?>(Decode(fromDatabase));
        }

        public Task<PagedResult<DepositApproval>> BrowseAsync(GetConsumerDepositApprovals query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            
            byte[][] depositApprovalsBytes = _database.GetAllValues().ToArray();
            if (depositApprovalsBytes.Length == 0)
            {
                return Task.FromResult(PagedResult<DepositApproval>.Empty);
            }

            DepositApproval[] depositApprovals = new DepositApproval[depositApprovalsBytes.Length];
            for (int i = 0; i < depositApprovalsBytes.Length; i++)
            {
                depositApprovals[i] = Decode(depositApprovalsBytes[i]);
            }

            IEnumerable<DepositApproval> filteredDepositApprovals = depositApprovals.AsEnumerable();
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

        public Task AddAsync(DepositApproval depositApproval) => AddOrUpdateAsync(depositApproval);
        public Task UpdateAsync(DepositApproval depositApproval) => AddOrUpdateAsync(depositApproval);

        private Task AddOrUpdateAsync(DepositApproval depositApproval)
        {
            Serialization.Rlp.Rlp rlp = _rlpDecoder.Encode(depositApproval);
            _database.Set(depositApproval.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private DepositApproval Decode(byte[] bytes)
            => _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}
