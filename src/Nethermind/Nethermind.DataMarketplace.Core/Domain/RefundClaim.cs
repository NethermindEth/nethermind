// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class RefundClaim
    {
        public Keccak DepositId { get; }
        public Keccak AssetId { get; }
        public uint Units { get; }
        public UInt256 Value { get; }
        public uint ExpiryTime { get; }
        public byte[] Pepper { get; }
        public Address Provider { get; }
        public Address RefundTo { get; }

        public RefundClaim(Keccak depositId, Keccak assetId, uint units, UInt256 value,
            uint expiryTime, byte[] pepper, Address provider, Address refundTo)
        {
            DepositId = depositId;
            AssetId = assetId;
            Units = units;
            Value = value;
            ExpiryTime = expiryTime;
            Pepper = pepper;
            Provider = provider;
            RefundTo = refundTo;
        }
    }
}
