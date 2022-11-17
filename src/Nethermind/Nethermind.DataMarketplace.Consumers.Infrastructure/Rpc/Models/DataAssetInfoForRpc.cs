// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DataAssetInfoForRpc
    {
        public DataAssetInfoForRpc(DataAssetInfo dataAsset)
        {
            Id = dataAsset.Id;
            Name = dataAsset.Name;
            Description = dataAsset.Description;
        }

        public Keccak Id { get; }
        public string Name { get; }
        public string Description { get; }
    }
}
