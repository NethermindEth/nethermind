/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.IO;
using System.Linq;
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataDeliveryReceiptRequestDecoder : IRlpDecoder<DataDeliveryReceiptRequest>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        private DataDeliveryReceiptRequestDecoder()
        {
        }

        static DataDeliveryReceiptRequestDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DataDeliveryReceiptRequest)] =
                new DataDeliveryReceiptRequestDecoder();
        }

        public DataDeliveryReceiptRequest Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var number = rlpStream.DecodeUInt();
            var depositId = rlpStream.DecodeKeccak();
            var unitsRange = Nethermind.Core.Encoding.Rlp.Decode<UnitsRange>(rlpStream);
            var isSettlement = rlpStream.DecodeBool();
            var receipts = Nethermind.Core.Encoding.Rlp.DecodeArray<DataDeliveryReceiptToMerge>(rlpStream);

            return new DataDeliveryReceiptRequest(number, depositId, unitsRange, isSettlement, receipts);
        }

        public Nethermind.Core.Encoding.Rlp Encode(DataDeliveryReceiptRequest item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Number),
                Nethermind.Core.Encoding.Rlp.Encode(item.DepositId),
                Nethermind.Core.Encoding.Rlp.Encode(item.UnitsRange),
                Nethermind.Core.Encoding.Rlp.Encode(item.IsSettlement),
                Nethermind.Core.Encoding.Rlp.Encode(item.ReceiptsToMerge.ToArray()));
        }
        
        public void Encode(MemoryStream stream, DataDeliveryReceiptRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DataDeliveryReceiptRequest item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}