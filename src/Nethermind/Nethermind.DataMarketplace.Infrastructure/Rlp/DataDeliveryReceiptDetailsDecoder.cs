// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
