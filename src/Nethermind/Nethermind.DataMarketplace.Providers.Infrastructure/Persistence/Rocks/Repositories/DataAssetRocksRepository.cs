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
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories
{
    internal class DataAssetRocksRepository : IDataAssetRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<DataAsset> _rlpDecoder;
        private  IRlpStreamDecoder<DataAsset> RlpStreamDecoder => (IRlpStreamDecoder<DataAsset>)_rlpDecoder;
        private  IRlpObjectDecoder<DataAsset> RlpObjectDecoder => (IRlpObjectDecoder<DataAsset>)_rlpDecoder;

        public DataAssetRocksRepository(IDb database, IRlpNdmDecoder<DataAsset> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder ?? throw new ArgumentNullException(nameof(rlpDecoder));
        }

        public Task<bool> ExistsAsync(Keccak id)
            => Task.FromResult(!(_database.Get(id) is null));

        public Task<DataAsset?> GetAsync(Keccak id)
        {
            byte[] fromDb = _database.Get(id);
            if (fromDb == null)
            {
                return Task.FromResult<DataAsset?>(null);
            }

            return Task.FromResult<DataAsset?>(Decode(fromDb));
        }

        public Task<PagedResult<DataAsset>> BrowseAsync(GetDataAssets query)
        {
            if (query is null)
            {
                return Task.FromResult(PagedResult<DataAsset>.Empty);
            }

            var dataAssetsBytes = _database.GetAllValues().ToArray();
            if (dataAssetsBytes.Length == 0)
            {
                return Task.FromResult(PagedResult<DataAsset>.Empty);
            }

            var dataAssets = new DataAsset[dataAssetsBytes.Length];
            for (var i = 0; i < dataAssetsBytes.Length; i++)
            {
                dataAssets[i] = Decode(dataAssetsBytes[i]);
            }

            if (!query.OnlyPublishable)
            {
                return Task.FromResult(dataAssets.OrderBy(h => h.Name).ToArray().Paginate(query));
            }

            return Task.FromResult(dataAssets.Where(c => c.State == DataAssetState.Published ||
                                                         c.State == DataAssetState.UnderMaintenance)
                .OrderBy(h => h.Name)
                .ToArray().ToArray().Paginate(query));
        }

        public Task AddAsync(DataAsset dataAsset) => AddOrUpdateAsync(dataAsset);
        public Task UpdateAsync(DataAsset dataAsset) => AddOrUpdateAsync(dataAsset);

        public Task RemoveAsync(Keccak id)
        {
            _database.Remove(id.Bytes);

            return Task.CompletedTask;
        }

        private Task AddOrUpdateAsync(DataAsset dataAsset)
        {
            var rlp = RlpObjectDecoder.Encode(dataAsset);
            _database.Set(dataAsset.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private DataAsset Decode(byte[] bytes)
            => RlpStreamDecoder.Decode(bytes.AsRlpStream());
    }
}