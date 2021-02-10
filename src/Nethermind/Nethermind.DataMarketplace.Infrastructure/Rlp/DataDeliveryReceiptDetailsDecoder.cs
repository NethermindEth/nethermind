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

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataDeliveryReceiptDetailsDecoder : IRlpNdmDecoder<DataDeliveryReceiptDetails>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }
        
        static DataDeliveryReceiptDetailsDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataDeliveryReceiptDetails)] =
                new DataDeliveryReceiptDetailsDecoder();
        }

        public DataDeliveryReceiptDetails Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Keccak id = rlpStream.DecodeKeccak();
            Keccak sessionId = rlpStream.DecodeKeccak();
            Keccak dataAssetId = rlpStream.DecodeKeccak();
            PublicKey consumerNodeId = new PublicKey(rlpStream.DecodeByteArray());
            DataDeliveryReceiptRequest request = Serialization.Rlp.Rlp.Decode<DataDeliveryReceiptRequest>(rlpStream);
            DataDeliveryReceipt receipt = Serialization.Rlp.Rlp.Decode<DataDeliveryReceipt>(rlpStream);
            ulong timestamp = rlpStream.DecodeUlong();
            bool isClaimed = rlpStream.DecodeBool();

            return new DataDeliveryReceiptDetails(id, sessionId, dataAssetId, consumerNodeId, request, receipt,
                timestamp, isClaimed);
        }

        public void Encode(RlpStream stream, DataDeliveryReceiptDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataDeliveryReceiptDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.SessionId),
                Serialization.Rlp.Rlp.Encode(item.DataAssetId),
                Serialization.Rlp.Rlp.Encode(item.ConsumerNodeId.Bytes),
                Serialization.Rlp.Rlp.Encode(item.Request),
                Serialization.Rlp.Rlp.Encode(item.Receipt),
                Serialization.Rlp.Rlp.Encode(item.Timestamp),
                Serialization.Rlp.Rlp.Encode(item.IsClaimed));
        }

        public int GetLength(DataDeliveryReceiptDetails item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
