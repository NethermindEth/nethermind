// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Mongo.Repositories
{
    internal class DataAssetMongoRepository : IDataAssetRepository
    {
        private readonly IMongoDatabase _database;

        public DataAssetMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<bool> ExistsAsync(Keccak id)
            => DataAssets.Find(c => c.Id == id).AnyAsync();

        public Task<DataAsset?> GetAsync(Keccak id)
            => DataAssets.Find(c => c.Id == id).FirstOrDefaultAsync();

        public async Task<PagedResult<DataAsset>> BrowseAsync(GetDataAssets query)
        {
            if (query is null)
            {
                return PagedResult<DataAsset>.Empty;
            }

            var headers = DataAssets.AsQueryable();
            if (query.OnlyPublishable)
            {
                headers = headers.Where(c => c.State == DataAssetState.Published ||
                                             c.State == DataAssetState.UnderMaintenance);
            }

            return await headers.OrderBy(h => h.Name).PaginateAsync(query);
        }

        public Task AddAsync(DataAsset dataAsset)
            => DataAssets.InsertOneAsync(dataAsset);

        public Task UpdateAsync(DataAsset dataAsset)
            => DataAssets.ReplaceOneAsync(c => c.Id == dataAsset.Id, dataAsset);

        public Task RemoveAsync(Keccak id)
            => DataAssets.DeleteOneAsync(c => c.Id == id);

        private IMongoCollection<DataAsset> DataAssets => _database.GetCollection<DataAsset>("dataAssets");
    }
}
