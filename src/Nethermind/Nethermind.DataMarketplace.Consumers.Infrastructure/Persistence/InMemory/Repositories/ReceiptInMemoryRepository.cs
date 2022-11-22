// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
