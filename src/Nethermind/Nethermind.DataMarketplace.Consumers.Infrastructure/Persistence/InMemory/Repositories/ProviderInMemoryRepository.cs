// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Consumers.Providers.Repositories;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories
{
    public class ProviderInMemoryRepository : IProviderRepository
    {
        private readonly DepositsInMemoryDb _db;

        public ProviderInMemoryRepository(DepositsInMemoryDb db)
        {
            _db = db;
        }

        public async Task<IReadOnlyList<DataAssetInfo>> GetDataAssetsAsync()
            => await Task.FromResult(_db.GetAll()
                .Select(d => new DataAssetInfo(d.DataAsset.Id, d.DataAsset.Name, d.DataAsset.Description))
                .Distinct()
                .ToList());

        public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync()
            => await Task.FromResult(_db.GetAll()
                .Select(d => new ProviderInfo(d.DataAsset.Provider.Name, d.DataAsset.Provider.Address))
                .Distinct()
                .ToList());
    }
}
