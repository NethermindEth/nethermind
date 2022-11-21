// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataAssetData
    {
        public Keccak AssetId { get; }
        public string? Data { get; }

        public DataAssetData(Keccak assetId, string? data)
        {
            AssetId = assetId;
            Data = data;
        }
    }
}
