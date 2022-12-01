// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataAssetProviderForRpc
    {
        public Address? Address { get; set; }
        public string? Name { get; set; }

        public DataAssetProviderForRpc()
        {
        }

        public DataAssetProviderForRpc(DataAssetProvider provider)
        {
            Address = provider.Address;
            Name = provider.Name;
        }
    }
}
