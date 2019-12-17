//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

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