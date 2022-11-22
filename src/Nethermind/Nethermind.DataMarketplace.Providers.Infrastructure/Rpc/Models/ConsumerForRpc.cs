// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class ConsumerForRpc
    {
        public Keccak DepositId { get; set; }
        public Address ConsumerAddress { get; set; }
        public Keccak DataAssetId { get; set; }
        public string DataAssetName { get; set; }
        public uint StartTimestamp { get; set; }
        public uint RequestedUnits { get; set; }
        public uint ConsumedUnits { get; set; }
        public string DepositValue { get; set; }
        public bool HasAvailableUnits { get; set; }

        public ConsumerForRpc(Consumer consumer)
        {
            DepositId = consumer.DepositId;
            ConsumerAddress = consumer.DataRequest.Consumer;
            DataAssetId = consumer.DataAsset.Id;
            DataAssetName = consumer.DataAsset.Name;
            StartTimestamp = consumer.VerificationTimestamp;
            RequestedUnits = consumer.DataRequest.Units;
            ConsumedUnits = consumer.ConsumedUnits;
            HasAvailableUnits = consumer.HasAvailableUnits;
            DepositValue = consumer.DataRequest.Value.ToString();
        }
    }
}
