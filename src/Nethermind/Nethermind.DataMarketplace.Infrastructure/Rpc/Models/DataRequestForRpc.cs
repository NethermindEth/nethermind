// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataRequestForRpc
    {
        public Keccak? DataAssetId { get; set; }
        public uint? Units { get; set; }
        public UInt256? Value { get; set; }
        public uint? ExpiryTime { get; set; }
        public Address? Provider { get; set; }

        public DataRequestForRpc()
        {
        }

        public DataRequestForRpc(DataRequest dataRequest)
        {
            DataAssetId = dataRequest.DataAssetId;
            Units = dataRequest.Units;
            Value = dataRequest.Value;
            ExpiryTime = dataRequest.ExpiryTime;
            Provider = dataRequest.Provider;
        }
    }
}
