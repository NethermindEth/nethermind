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
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataDeliveryReceiptDecoder : IRlpDecoder<DataDeliveryReceipt>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        private DataDeliveryReceiptDecoder()
        {
        }

        static DataDeliveryReceiptDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DataDeliveryReceipt)] = new DataDeliveryReceiptDecoder();
        }

        public DataDeliveryReceipt Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var statusCode = (StatusCodes) rlpStream.DecodeInt();
            var consumedUnits = rlpStream.DecodeUInt();
            var unpaidUnits = rlpStream.DecodeUInt();
            var signature = SignatureDecoder.DecodeSignature(rlpStream);

            return new DataDeliveryReceipt(statusCode, consumedUnits, unpaidUnits, signature);
        }

        public Nethermind.Core.Encoding.Rlp Encode(DataDeliveryReceipt item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode((int) item.StatusCode),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConsumedUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.UnpaidUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.Signature.V),
                Nethermind.Core.Encoding.Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Nethermind.Core.Encoding.Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }
        
        public void Encode(MemoryStream stream, DataDeliveryReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DataDeliveryReceipt item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}