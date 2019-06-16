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

        public Task<DataDeliveryReceiptDetails> GetAsync(Keccak id)
            => Receipts.Find(c => c.Id == id).FirstOrDefaultAsync();

        public async Task<IReadOnlyList<DataDeliveryReceiptDetails>> BrowseAsync(Keccak depositId = null,
            Keccak dataHeaderId = null, Keccak sessionId = null)
        {
            var receipts = Receipts.AsQueryable();
            if (!(depositId is null))
            {
                receipts = receipts.Where(c => c.DepositId == depositId);
            }
            
            if (!(dataHeaderId is null))
            {
                receipts = receipts.Where(c => c.DataHeaderId == dataHeaderId);
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