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

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P.Subprotocols.Tru
{
    public class ReceiptsMessageSerializer : IMessageSerializer<ReceiptsMessage>
    {
        [Todo(Improve.MissingFunctionality, "When serializing receipts we need either to just get raw receipts from storage that were stored in the bare format or recognize the block number from spec provider")]
        public byte[] Serialize(ReceiptsMessage message)
        {
            if (message.Receipts == null) return Rlp.OfEmptySequence.Bytes;
            return Rlp.Encode(message.Receipts.Select(b => b == null ? Rlp.OfEmptySequence : Rlp.Encode(b.Select(n => n == null ? Rlp.OfEmptySequence : Rlp.Encode(n, RlpBehaviors.Storage)).ToArray())).ToArray()).Bytes;
        }

        public ReceiptsMessage Deserialize(byte[] bytes)
        {
            if (bytes.Length == 0 && bytes[0] == Rlp.OfEmptySequence[0]) return new ReceiptsMessage(null);

            Rlp.DecoderContext decoderContext = bytes.AsRlpContext();

            var data = decoderContext.DecodeArray(itemContext =>
                itemContext.DecodeArray(nestedContext => Rlp.Decode<TransactionReceipt>(nestedContext, RlpBehaviors.Storage)) ?? new TransactionReceipt[0]);
            ReceiptsMessage message = new ReceiptsMessage(data);

            return message;
        }
    }
}