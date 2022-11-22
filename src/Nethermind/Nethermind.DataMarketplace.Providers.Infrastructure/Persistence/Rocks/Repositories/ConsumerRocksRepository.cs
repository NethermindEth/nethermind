// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private IRlpStreamDecoder<Consumer> RlpStreamDecoder => (IRlpStreamDecoder<Consumer>)_rlpDecoder;
        private IRlpObjectDecoder<Consumer> RlpObjectDecoder => (IRlpObjectDecoder<Consumer>)_rlpDecoder;

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
