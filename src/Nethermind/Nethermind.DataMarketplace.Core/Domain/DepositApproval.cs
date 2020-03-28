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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DepositApproval
    {
        public Keccak Id { get; private set; }
        public Keccak AssetId { get; private set; }
        public string AssetName { get; private set; }
        public string Kyc { get; private set; }
        public Address Consumer { get; private set; }
        public Address Provider { get; private set; }
        public ulong Timestamp { get; private set; }
        public DepositApprovalState State { get; private set; }

        public static Keccak CalculateId(Keccak assetId, Address consumer)
        {
            return Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(consumer)).Bytes);
        }

        public DepositApproval(
            Keccak assetId,
            string assetName,
            string kyc,
            Address consumer,
            Address provider,
            ulong timestamp,
            DepositApprovalState state)
        {
            AssetId = assetId ?? throw new ArgumentNullException(nameof(assetId));
            AssetName = assetName ?? throw new ArgumentNullException(nameof(assetName));
            Kyc = kyc ?? throw new ArgumentNullException(nameof(kyc));
            Consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Timestamp = timestamp;
            State = state;
            Id = CalculateId(assetId, consumer);
        }

        public void Confirm()
        {
            State = DepositApprovalState.Confirmed;
        }

        public void Reject()
        {
            State = DepositApprovalState.Rejected;
        }
    }
}