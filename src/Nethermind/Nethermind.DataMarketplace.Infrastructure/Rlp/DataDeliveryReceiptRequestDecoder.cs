// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataDeliveryReceiptRequestDecoder : IRlpNdmDecoder<DataDeliveryReceiptRequest>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataDeliveryReceiptRequestDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataDeliveryReceiptRequest)] =
                new DataDeliveryReceiptRequestDecoder();
        }

        public DataDeliveryReceiptRequest Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            uint number = rlpStream.DecodeUInt();
            Keccak depositId = rlpStream.DecodeKeccak();
            UnitsRange unitsRange = Serialization.Rlp.Rlp.Decode<UnitsRange>(rlpStream);
            bool isSettlement = rlpStream.DecodeBool();
            var receipts = Serialization.Rlp.Rlp.DecodeArray<DataDeliveryReceiptToMerge>(rlpStream);

            return new DataDeliveryReceiptRequest(number, depositId, unitsRange, isSettlement, receipts);
        }

        public void Encode(RlpStream stream, DataDeliveryReceiptRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataDeliveryReceiptRequest item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Number),
                Serialization.Rlp.Rlp.Encode(item.DepositId),
                Serialization.Rlp.Rlp.Encode(item.UnitsRange),
                Serialization.Rlp.Rlp.Encode(item.IsSettlement),
                Serialization.Rlp.Rlp.Encode(item.ReceiptsToMerge.ToArray()));
        }

        public int GetLength(DataDeliveryReceiptRequest item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
