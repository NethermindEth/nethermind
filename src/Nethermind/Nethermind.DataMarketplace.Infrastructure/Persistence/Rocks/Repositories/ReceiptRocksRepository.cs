// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories
{
    public class ReceiptRocksRepository : IReceiptRepository
    {
        private readonly IDb _database;
        private readonly IRlpNdmDecoder<DataDeliveryReceiptDetails> _rlpDecoder;

        public ReceiptRocksRepository(IDb database, IRlpNdmDecoder<DataDeliveryReceiptDetails> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public Task<DataDeliveryReceiptDetails?> GetAsync(Keccak id)
        {
            byte[] bytes = _database.Get(id);
            return bytes == null ? Task.FromResult<DataDeliveryReceiptDetails?>(null) : Task.FromResult<DataDeliveryReceiptDetails?>(Decode(bytes));
        }


        public async Task<IReadOnlyList<DataDeliveryReceiptDetails>> BrowseAsync(Keccak? depositId = null, Keccak? dataAssetId = null, Keccak? sessionId = null)
        {
            var receiptsBytes = _database.GetAllValues().ToArray();
            if (receiptsBytes.Length == 0)
            {
                return Array.Empty<DataDeliveryReceiptDetails>();
            }

            await Task.CompletedTask;
            var receipts = new DataDeliveryReceiptDetails[receiptsBytes.Length];
            for (var i = 0; i < receiptsBytes.Length; i++)
            {
                receipts[i] = Decode(receiptsBytes[i]);
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
            => _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}
