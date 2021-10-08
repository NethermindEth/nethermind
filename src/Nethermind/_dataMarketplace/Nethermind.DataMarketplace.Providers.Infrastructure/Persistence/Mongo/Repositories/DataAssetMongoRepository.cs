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