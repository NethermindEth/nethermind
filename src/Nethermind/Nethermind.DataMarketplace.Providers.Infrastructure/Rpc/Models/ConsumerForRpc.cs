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