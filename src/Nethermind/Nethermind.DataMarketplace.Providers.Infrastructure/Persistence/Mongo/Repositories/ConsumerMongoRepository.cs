// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
