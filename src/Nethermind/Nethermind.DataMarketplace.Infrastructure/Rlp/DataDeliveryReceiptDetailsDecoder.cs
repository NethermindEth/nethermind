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
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataDeliveryReceiptDetailsDecoder : IRlpDecoder<DataDeliveryReceiptDetails>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        public DataDeliveryReceiptDetailsDecoder()
        {
        }

        static DataDeliveryReceiptDetailsDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DataDeliveryReceiptDetails)] =
                new DataDeliveryReceiptDetailsDecoder();
        }

        public DataDeliveryReceiptDetails Decode(Nethermind.Core.Encoding.Rlp.DecoderContext context,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = context.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var id = context.DecodeKeccak();
            var sessionId = context.DecodeKeccak();
            var dataHeaderId = context.DecodeKeccak();
            var consumerNodeId = new PublicKey(context.DecodeByteArray());
            var request = Nethermind.Core.Encoding.Rlp.Decode<DataDeliveryReceiptRequest>(context);
            var receipt = Nethermind.Core.Encoding.Rlp.Decode<DataDeliveryReceipt>(context);
            var timestamp = context.DecodeUlong();
            var isClaimed = context.DecodeBool();

            return new DataDeliveryReceiptDetails(id, sessionId, dataHeaderId, consumerNodeId, request, receipt,
                timestamp, isClaimed);
        }

        public Nethermind.Core.Encoding.Rlp Encode(DataDeliveryReceiptDetails item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Id),
                Nethermind.Core.Encoding.Rlp.Encode(item.SessionId),
                Nethermind.Core.Encoding.Rlp.Encode(item.DataHeaderId),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConsumerNodeId.Bytes),
                Nethermind.Core.Encoding.Rlp.Encode(item.Request),
                Nethermind.Core.Encoding.Rlp.Encode(item.Receipt),
                Nethermind.Core.Encoding.Rlp.Encode(item.Timestamp),
                Nethermind.Core.Encoding.Rlp.Encode(item.IsClaimed));
        }

        public void Encode(MemoryStream stream, DataDeliveryReceiptDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DataDeliveryReceiptDetails item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}