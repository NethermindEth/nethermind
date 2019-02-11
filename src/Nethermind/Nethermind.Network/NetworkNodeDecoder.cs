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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network
{
    public class NetworkNodeDecoder : IRlpDecoder<NetworkNode>
    {
        static NetworkNodeDecoder()
        {
            Rlp.Decoders[typeof(NetworkNode)] = new NetworkNodeDecoder();
        }

        public NetworkNode Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            context.ReadSequenceLength();

            var publicKey = new PublicKey(context.DecodeByteArray());
            var ip = System.Text.Encoding.UTF8.GetString(context.DecodeByteArray());
            var port = context.DecodeByteArray().ToInt32();
            System.Text.Encoding.UTF8.GetString(context.DecodeByteArray());
            var reputation = context.DecodeByteArray().ToInt64();

            var networkNode = new NetworkNode(publicKey, ip != string.Empty ? ip : null, port, reputation);
            return networkNode;
        }

        public Rlp Encode(NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Rlp[] elements = new Rlp[5];
            elements[0] = Rlp.Encode(item.NodeId.Bytes);
            elements[1] = Rlp.Encode(item.Host);
            elements[2] = Rlp.Encode(item.Port);
            elements[3] = Rlp.Encode(string.Empty);
            elements[4] = Rlp.Encode(item.Reputation);
            return Rlp.Encode(elements);
        }

        public static void Init()
        {
            // here to register with RLP in static constructor
        }
    }
}