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
