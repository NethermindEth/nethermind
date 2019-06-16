using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Store;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories
{
    public class ReceiptRocksRepository : IReceiptRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<DataDeliveryReceiptDetails> _rlpDecoder;

        public ReceiptRocksRepository(IDb database, IRlpDecoder<DataDeliveryReceiptDetails> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public Task<DataDeliveryReceiptDetails> GetAsync(Keccak id)
            => Task.FromResult(Decode(_database.Get(id)));

        public async Task<IReadOnlyList<DataDeliveryReceiptDetails>> BrowseAsync(Keccak depositId = null,
            Keccak dataHeaderId = null, Keccak sessionId = null)
        {
            var receiptsBytes = _database.GetAll();
            if (receiptsBytes.Length == 0)
            {
                return Array.Empty<DataDeliveryReceiptDetails>();
            }

            await Task.CompletedTask;
            var consumers = new DataDeliveryReceiptDetails[receiptsBytes.Length];
            for (var i = 0; i < receiptsBytes.Length; i++)
            {
                consumers[i] = Decode(receiptsBytes[i]);
            }

            var filteredReceipts = consumers.AsEnumerable();
            if (!(depositId is null))
            {
                filteredReceipts = filteredReceipts.Where(c => c.DepositId == depositId);
            }
            
            if (!(dataHeaderId is null))
            {
                filteredReceipts = filteredReceipts.Where(c => c.DataHeaderId == dataHeaderId);
            }

            if (!(sessionId is null))
            {
                filteredReceipts = filteredReceipts.Where(c => c.SessionId == sessionId);
            }

            return filteredReceipts.ToArray();
        }

        public Task AddAsync(DataDeliveryReceiptDetails receipt) => AddOrUpdateAsync(receipt);

        public Task UpdateAsync(DataDeliveryReceiptDetails receipt) => AddOrUpdateAsync(receipt);

        private Task AddOrUpdateAsync(DataDeliveryReceiptDetails receipt)
        {
            var rlp = _rlpDecoder.Encode(receipt);
            _database.Set(receipt.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private DataDeliveryReceiptDetails Decode(byte[] bytes)
            => bytes is null
                ? null
                : _rlpDecoder.Decode(bytes.AsRlpContext());
    }
}