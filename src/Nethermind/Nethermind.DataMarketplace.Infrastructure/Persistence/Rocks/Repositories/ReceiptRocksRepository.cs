/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
            Keccak dataAssetId = null, Keccak sessionId = null)
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
            => bytes is null
                ? null
                : _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}