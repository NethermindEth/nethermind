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
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DepositApprovalDecoder : IRlpNdmDecoder<DepositApproval>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DepositApprovalDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DepositApproval)] = new DepositApprovalDecoder();
        }

        public DepositApproval Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Keccak assetId = rlpStream.DecodeKeccak();
            string assetName = rlpStream.DecodeString();
            string kyc = rlpStream.DecodeString();
            Address consumer = rlpStream.DecodeAddress();
            Address provider = rlpStream.DecodeAddress();
            ulong timestamp = rlpStream.DecodeUlong();
            DepositApprovalState state = (DepositApprovalState) rlpStream.DecodeInt();

            return new DepositApproval(assetId, assetName, kyc, consumer, provider, timestamp, state);
        }

        public void Encode(RlpStream stream, DepositApproval item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DepositApproval item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.AssetId),
                Serialization.Rlp.Rlp.Encode(item.AssetName),
                Serialization.Rlp.Rlp.Encode(item.Kyc),
                Serialization.Rlp.Rlp.Encode(item.Consumer),
                Serialization.Rlp.Rlp.Encode(item.Provider),
                Serialization.Rlp.Rlp.Encode(item.Timestamp),
                Serialization.Rlp.Rlp.Encode((int) item.State));
        }

        public int GetLength(DepositApproval item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
