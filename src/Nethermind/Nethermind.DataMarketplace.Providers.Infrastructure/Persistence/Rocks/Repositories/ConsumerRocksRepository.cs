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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories
{
    internal class ConsumerRocksRepository : IConsumerRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<Consumer> _rlpDecoder;
        private  IRlpStreamDecoder<Consumer> RlpStreamDecoder => (IRlpStreamDecoder<Consumer>)_rlpDecoder;
        private  IRlpObjectDecoder<Consumer> RlpObjectDecoder => (IRlpObjectDecoder<Consumer>)_rlpDecoder;

        public ConsumerRocksRepository(IDb database, IRlpNdmDecoder<Consumer> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder ?? throw new ArgumentNullException(nameof(rlpDecoder));
        }

        public Task<Consumer?> GetAsync(Keccak depositId)
        {
            byte[] fromDb = _database.Get(depositId);
            if (fromDb == null)
            {
                return Task.FromResult<Consumer?>(null);
            }
            
            return Task.FromResult<Consumer?>(Decode(fromDb));   
        }

        public Task<PagedResult<Consumer>> BrowseAsync(GetConsumers? query)
        {
            if (query is null)
            {
                return Task.FromResult(PagedResult<Consumer>.Empty);
            }

            var consumersBytes = _database.GetAllValues().ToArray();
            if (consumersBytes.Length == 0)
            {
                return Task.FromResult(PagedResult<Consumer>.Empty);
            }

            var consumers = new Consumer[consumersBytes.Length];
            for (var i = 0; i < consumersBytes.Length; i++)
            {
                consumers[i] = Decode(consumersBytes[i]);
            }

            var filteredConsumers = consumers.AsEnumerable();
            if (!(query.AssetId is null))
            {
                filteredConsumers = filteredConsumers.Where(c => c.DataAsset.Id == query.AssetId);
            }

            if (!(query.Address is null))
            {
                filteredConsumers = filteredConsumers.Where(c => c.DataRequest.Consumer == query.Address);
            }

            if (query.OnlyWithAvailableUnits)
            {
                filteredConsumers = filteredConsumers.Where(c => c.HasAvailableUnits == query.OnlyWithAvailableUnits);
            }

            return Task.FromResult(filteredConsumers.OrderByDescending(c => c.VerificationTimestamp).ToArray().Paginate(query));
        }

        public Task AddAsync(Consumer consumer) => AddOrUpdateAsync(consumer);

        public Task UpdateAsync(Consumer consumer) => AddOrUpdateAsync(consumer);

        private Task AddOrUpdateAsync(Consumer consumer)
        {
            var rlp = RlpObjectDecoder.Encode(consumer);
            _database.Set(consumer.DepositId, rlp.Bytes);

            return Task.CompletedTask;
        }

        private Consumer Decode(byte[] bytes)
            => RlpStreamDecoder.Decode(bytes.AsRlpStream());
    }
}