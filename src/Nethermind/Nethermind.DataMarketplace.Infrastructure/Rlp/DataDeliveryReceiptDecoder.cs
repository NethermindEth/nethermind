// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataDeliveryReceiptDecoder : IRlpNdmDecoder<DataDeliveryReceipt>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataDeliveryReceiptDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataDeliveryReceipt)] = new DataDeliveryReceiptDecoder();
        }

        public DataDeliveryReceipt Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                StatusCodes statusCode = (StatusCodes)rlpStream.DecodeInt();
                uint consumedUnits = rlpStream.DecodeUInt();
                uint unpaidUnits = rlpStream.DecodeUInt();
                Signature signature = SignatureDecoder.DecodeSignature(rlpStream);

                return new DataDeliveryReceipt(statusCode, consumedUnits, unpaidUnits, signature);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(DataDeliveryReceiptDecoder)} could not be decoded", e);
            }
        }

        public void Encode(RlpStream stream, DataDeliveryReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataDeliveryReceipt item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode((int)item.StatusCode),
                Serialization.Rlp.Rlp.Encode(item.ConsumedUnits),
                Serialization.Rlp.Rlp.Encode(item.UnpaidUnits),
                Serialization.Rlp.Rlp.Encode(item.Signature.V),
                Serialization.Rlp.Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Serialization.Rlp.Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }

        public int GetLength(DataDeliveryReceipt item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
