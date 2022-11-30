// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class ConsumerDetailsForRpc : ConsumerForRpc
    {
        public DataRequestForRpc DataRequest { get; set; }
        public DataAssetForRpc DataAsset { get; set; }
        public uint UnclaimedUnits { get; set; }

        public ConsumerDetailsForRpc(Consumer consumer, uint unclaimedUnits) : base(consumer)
        {
            DataRequest = new DataRequestForRpc(consumer.DataRequest);
            DataAsset = new DataAssetForRpc(consumer.DataAsset);
            UnclaimedUnits = unclaimedUnits;
        }
    }
}
