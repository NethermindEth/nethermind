// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataRequest
    {
        public Keccak DataAssetId { get; }
        public uint Units { get; }
        public UInt256 Value { get; }
        public uint ExpiryTime { get; }
        public byte[] Pepper { get; }
        public Address Provider { get; }
        public Address Consumer { get; }
        public Signature Signature { get; }

        public DataRequest(Keccak dataAssetId, uint units, UInt256 value, uint expiryTime, byte[] pepper,
            Address provider, Address consumer, Signature signature)
        {
            DataAssetId = dataAssetId;
            Units = units;
            Value = value;
            ExpiryTime = expiryTime;
            Pepper = pepper;
            Provider = provider;
            Consumer = consumer;
            Signature = signature;
        }
    }
}
