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

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class ReceiptsMessageSerializer : IMessageSerializer<ReceiptsMessage>
    {
        private readonly ISpecProvider _specProvider;

        public ReceiptsMessageSerializer(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }
        
        [Todo(Improve.MissingFunctionality, "When serializing receipts we need either to just get raw receipts from storage that were stored in the bare format or recognize the block number from spec provider")]
        public byte[] Serialize(ReceiptsMessage message)
        {
            if (message.TxReceipts == null) return Rlp.OfEmptySequence.Bytes;
            return Rlp.Encode(message.TxReceipts.Select(b => b == null ? Rlp.OfEmptySequence : Rlp.Encode(b.Select(n => n == null ? Rlp.OfEmptySequence : Rlp.Encode(n, _specProvider.GetSpec(n.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)).ToArray())).ToArray()).Bytes;
        }

        public ReceiptsMessage Deserialize(byte[] bytes)
        {
            if (bytes.Length == 0 && bytes[0] == Rlp.OfEmptySequence[0]) return new ReceiptsMessage(null);

            RlpStream rlpStream = bytes.AsRlpStream();

            var data = rlpStream.DecodeArray(itemContext =>
                itemContext.DecodeArray(nestedContext => Rlp.Decode<TxReceipt>(nestedContext)) ?? new TxReceipt[0]);
            ReceiptsMessage message = new ReceiptsMessage(data);
            
            return message;
        }
    }
}