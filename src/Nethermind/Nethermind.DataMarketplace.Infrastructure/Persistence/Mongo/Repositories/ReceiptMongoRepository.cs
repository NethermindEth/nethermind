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