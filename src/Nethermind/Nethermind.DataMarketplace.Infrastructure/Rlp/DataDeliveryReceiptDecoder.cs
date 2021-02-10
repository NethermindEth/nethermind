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
                StatusCodes statusCode = (StatusCodes) rlpStream.DecodeInt();
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
                Serialization.Rlp.Rlp.Encode((int) item.StatusCode),
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
