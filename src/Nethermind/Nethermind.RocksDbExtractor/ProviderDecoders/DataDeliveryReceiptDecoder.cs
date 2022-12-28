// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.RocksDbExtractor.ProviderDecoders
{
    public class DataDeliveryReceiptDecoder : IRlpDecoder<DataDeliveryReceipt>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataDeliveryReceiptDecoder()
        {
            Rlp.Decoders[typeof(DataDeliveryReceipt)] = new DataDeliveryReceiptDecoder();
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

        public Rlp Encode(DataDeliveryReceipt item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            return Rlp.Encode(
                Rlp.Encode((int)item.StatusCode),
                Rlp.Encode(item.ConsumedUnits),
                Rlp.Encode(item.UnpaidUnits),
                Rlp.Encode(item.Signature.V),
                Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }

        public int GetLength(DataDeliveryReceipt item, RlpBehaviors rlpBehaviors)
        {
            throw new NotImplementedException();
        }
    }
}
