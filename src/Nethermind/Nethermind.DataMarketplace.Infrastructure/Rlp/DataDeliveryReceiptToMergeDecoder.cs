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
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataDeliveryReceiptToMergeDecoder : IRlpDecoder<DataDeliveryReceiptToMerge>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        private DataDeliveryReceiptToMergeDecoder()
        {
        }

        static DataDeliveryReceiptToMergeDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DataDeliveryReceiptToMerge)] =
                new DataDeliveryReceiptToMergeDecoder();
        }

        public DataDeliveryReceiptToMerge Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var unitsRange = Nethermind.Core.Encoding.Rlp.Decode<UnitsRange>(rlpStream);
            var signature = SignatureDecoder.DecodeSignature(rlpStream);

            return new DataDeliveryReceiptToMerge(unitsRange, signature);
        }

        public Nethermind.Core.Encoding.Rlp Encode(DataDeliveryReceiptToMerge item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.UnitsRange),
                Nethermind.Core.Encoding.Rlp.Encode(item.Signature.V),
                Nethermind.Core.Encoding.Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Nethermind.Core.Encoding.Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }

        public void Encode(MemoryStream stream, DataDeliveryReceiptToMerge item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DataDeliveryReceiptToMerge item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}