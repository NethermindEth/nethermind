// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class DataAssetDataForRpc
    {
        public Keccak? AssetId { get; set; }
        public string? Data { get; set; }
    }
}
