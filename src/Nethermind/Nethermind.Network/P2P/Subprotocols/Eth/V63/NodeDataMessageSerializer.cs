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
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class NodeDataMessageSerializer : IMessageSerializer<NodeDataMessage>
    {
        public byte[] Serialize(NodeDataMessage message)
        {
            if (message.Data == null)
            {
                return Rlp.OfEmptySequence.Bytes;
            }
            
            return Rlp.Encode(message.Data.Select(b => b == null ? Rlp.OfEmptyByteArray : Rlp.Encode(b)).ToArray()).Bytes;
        }

        public NodeDataMessage Deserialize(byte[] bytes)
        {
            if (bytes.Length == 0 && bytes[0] == Rlp.OfEmptySequence[0])
            {
                return new NodeDataMessage(null);
            }
            
            RlpStream rlpStream = bytes.AsRlpStream();

            var data = rlpStream.DecodeArray(itemContext => itemContext.DecodeByteArray());
            NodeDataMessage message = new NodeDataMessage(data);

            return message;
        }
    }
}