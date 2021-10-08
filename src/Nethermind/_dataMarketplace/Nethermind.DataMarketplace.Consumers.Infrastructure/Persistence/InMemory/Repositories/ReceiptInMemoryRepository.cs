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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories
{
    public class ReceiptInMemoryRepository : IReceiptRepository
    {
        private readonly ConcurrentDictionary<Keccak, DataDeliveryReceiptDetails> _db =
            new ConcurrentDictionary<Keccak, DataDeliveryReceiptDetails>();

        public Task<DataDeliveryReceiptDetails?> GetAsync(Keccak id)
            => Task.FromResult(_db.TryGetValue(id, out DataDeliveryReceiptDetails? receipt) ? receipt : null);

        public async Task<IReadOnlyList<DataDeliveryReceiptDetails>> BrowseAsync(
            Keccak? depositId = null,
            Keccak? dataAssetId = null,
            Keccak? sessionId = null)
        {
            var receipts = _db.Values;
            if (!receipts.Any())
            {
                return Array.Empty<DataDeliveryReceiptDetails>();
            }

            var filteredReceipts = receipts.AsEnumerable();
            if (!(depositId is null))
            {
                filteredReceipts = filteredReceipts.Where(c => c.DepositId == depositId);
            }
            
            if (!(dataAssetId is null))
            {
                filteredReceipts = filteredReceipts.Where(c => c.DataAssetId == dataAssetId);
            }

            if (!(sessionId is null))
            {
                filteredReceipts = filteredReceipts.Where(c => c.SessionId == sessionId);
            }

            await Task.CompletedTask;

            return filteredReceipts.ToArray();
        }

        public Task AddAsync(DataDeliveryReceiptDetails receipt)
        {
            _db.TryAdd(receipt.Id, receipt);

            return Task.CompletedTask;
        }

        public Task UpdateAsync(DataDeliveryReceiptDetails receipt) => Task.CompletedTask;
    }
}