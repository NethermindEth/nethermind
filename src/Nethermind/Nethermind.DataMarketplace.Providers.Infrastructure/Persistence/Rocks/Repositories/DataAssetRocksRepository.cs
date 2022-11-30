// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private IRlpStreamDecoder<DataAsset> RlpStreamDecoder => (IRlpStreamDecoder<DataAsset>)_rlpDecoder;
        private IRlpObjectDecoder<DataAsset> RlpObjectDecoder => (IRlpObjectDecoder<DataAsset>)_rlpDecoder;

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
