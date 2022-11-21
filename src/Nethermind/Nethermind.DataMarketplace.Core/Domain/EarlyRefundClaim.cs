// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class EarlyRefundClaim
    {

        public Keccak DepositId { get; }
        public Keccak AssetId { get; }
        public uint Units { get; }
        public UInt256 Value { get; }
        public uint ExpiryTime { get; }
        public byte[] Pepper { get; }
        public Address Provider { get; }
        public uint ClaimableAfter { get; }
        public Signature Signature { get; }
        public Address RefundTo { get; }

        public EarlyRefundClaim(Keccak depositId, Keccak assetId, uint units, UInt256 value, uint expiryTime,
            byte[] pepper, Address provider, uint claimableAfter, Signature signature, Address refundTo)
        {
            DepositId = depositId;
            AssetId = assetId;
            Units = units;
            Value = value;
            ExpiryTime = expiryTime;
            Pepper = pepper;
            Provider = provider;
            ClaimableAfter = claimableAfter;
            Signature = signature;
            RefundTo = refundTo;
        }
    }
}
