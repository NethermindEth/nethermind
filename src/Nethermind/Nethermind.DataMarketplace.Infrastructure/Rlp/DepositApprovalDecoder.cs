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

using System.IO;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DepositApprovalDecoder : IRlpDecoder<DepositApproval>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        public DepositApprovalDecoder()
        {
        }

        static DepositApprovalDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DepositApproval)] = new DepositApprovalDecoder();
        }

        public DepositApproval Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var id = rlpStream.DecodeKeccak();
            var assetId = rlpStream.DecodeKeccak();
            var assetName = rlpStream.DecodeString();
            var kyc = rlpStream.DecodeString();
            var consumer = rlpStream.DecodeAddress();
            var provider = rlpStream.DecodeAddress();
            var timestamp = rlpStream.DecodeUlong();
            var state = (DepositApprovalState) rlpStream.DecodeInt();

            return new DepositApproval(id, assetId, assetName, kyc, consumer, provider, timestamp, state);
        }

        public Serialization.Rlp.Rlp Encode(DepositApproval item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.AssetId),
                Serialization.Rlp.Rlp.Encode(item.AssetName),
                Serialization.Rlp.Rlp.Encode(item.Kyc),
                Serialization.Rlp.Rlp.Encode(item.Consumer),
                Serialization.Rlp.Rlp.Encode(item.Provider),
                Serialization.Rlp.Rlp.Encode(item.Timestamp),
                Serialization.Rlp.Rlp.Encode((int) item.State));
        }

        public void Encode(MemoryStream stream, DepositApproval item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DepositApproval item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}