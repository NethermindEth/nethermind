// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;

namespace Nethermind.DataMarketplace.Consumers.Providers.Repositories
{
    public interface IProviderRepository
    {
        Task<IReadOnlyList<DataAssetInfo>> GetDataAssetsAsync();
        Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync();
    }
}
