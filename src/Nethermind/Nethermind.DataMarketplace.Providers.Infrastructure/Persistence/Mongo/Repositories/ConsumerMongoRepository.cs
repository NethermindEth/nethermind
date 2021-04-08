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

using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories
{
    internal class ConsumerMongoRepository : IConsumerRepository
    {
        private readonly IMongoDatabase _database;

        public ConsumerMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<Consumer?> GetAsync(Keccak depositId)
            => Consumers.Find(c => c.DepositId == depositId).FirstOrDefaultAsync();

        public async Task<PagedResult<Consumer>> BrowseAsync(GetConsumers query)
        {
            if (query is null)
            {
                return PagedResult<Consumer>.Empty;
            }
            
            var consumers = Consumers.AsQueryable();
            if (!(query.AssetId is null))
            {
                consumers = consumers.Where(c => c.DataRequest.DataAssetId == query.AssetId);
            }

            if (!(query.Address is null))
            {
                consumers = consumers.Where(c => c.DataRequest.Consumer == query.Address);
            }

            if (query.OnlyWithAvailableUnits)
            {
                consumers = consumers.Where(c => c.HasAvailableUnits == query.OnlyWithAvailableUnits);
            }

            return await consumers.OrderByDescending(c => c.VerificationTimestamp).PaginateAsync(query);
        }

        public Task AddAsync(Consumer consumer)
            => Consumers.InsertOneAsync(consumer);

        public Task UpdateAsync(Consumer consumer)
            => Consumers.ReplaceOneAsync(c => c.DepositId == consumer.DepositId, consumer);

        private IMongoCollection<Consumer> Consumers => _database.GetCollection<Consumer>("consumers");
    }
}