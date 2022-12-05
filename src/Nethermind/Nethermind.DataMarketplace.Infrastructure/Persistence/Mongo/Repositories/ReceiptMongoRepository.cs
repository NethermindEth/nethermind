// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories
{
    public class ReceiptMongoRepository : IReceiptRepository
    {
        private readonly IMongoDatabase _database;
        private readonly string _collectionName;

        public ReceiptMongoRepository(IMongoDatabase database, string collectionName = "receipts")
        {
            _database = database;
            _collectionName = collectionName;
        }

        public Task<DataDeliveryReceiptDetails?> GetAsync(Keccak id)
        {
            return Receipts.Find(c => c.Id == id).FirstOrDefaultAsync<DataDeliveryReceiptDetails>()!;
        }

        public async Task<IReadOnlyList<DataDeliveryReceiptDetails>> BrowseAsync(Keccak? depositId = null, Keccak? dataAssetId = null, Keccak? sessionId = null)
        {
            var receipts = Receipts.AsQueryable();
            if (!(depositId is null))
            {
                receipts = receipts.Where(c => c.DepositId == depositId);
            }

            if (!(dataAssetId is null))
            {
                receipts = receipts.Where(c => c.DataAssetId == dataAssetId);
            }

            if (!(sessionId is null))
            {
                receipts = receipts.Where(c => c.SessionId == sessionId);
            }

            return await receipts.OrderBy(s => s.Number).ToListAsync();
        }

        public Task AddAsync(DataDeliveryReceiptDetails receipt)
            => Receipts.InsertOneAsync(receipt);

        public Task UpdateAsync(DataDeliveryReceiptDetails receipt)
            => Receipts.ReplaceOneAsync(r => r.DepositId == receipt.DepositId && r.Number == receipt.Number, receipt);

        private IMongoCollection<DataDeliveryReceiptDetails> Receipts
            => _database.GetCollection<DataDeliveryReceiptDetails>(_collectionName);
    }
}
