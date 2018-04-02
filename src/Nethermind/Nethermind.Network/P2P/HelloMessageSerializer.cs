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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P
{
    public class HelloMessageSerializer : IMessageSerializer<HelloMessage>
    {
        public byte[] Serialize(HelloMessage message)
        {
            return Rlp.Encode(
                message.P2PVersion,
                message.ClientId,
                message.Capabilities.Select(c => Rlp.Encode(c.Key.ToLowerInvariant(), c.Value)).ToArray(),
                message.ListenPort,
                message.NodeId.PrefixedBytes
            ).Bytes;
        }

        public HelloMessage Deserialize(byte[] bytes)
        {
            DecodedRlp decoded = Rlp.Decode(new Rlp(bytes));
            HelloMessage helloMessage = new HelloMessage();
            helloMessage.P2PVersion = decoded.GetByte(0);
            helloMessage.ClientId = decoded.GetString(1);
            helloMessage.Capabilities = new Dictionary<string, int>();
            DecodedRlp decodedCapabilities = decoded.GetSequence(2);
            for (int i = 0; i < decodedCapabilities.Length; i++)
            {
                DecodedRlp capability = decodedCapabilities.GetSequence(i);
                string name = capability.GetString(0);
                int version = capability.GetByte(1);
                helloMessage.Capabilities.Add(name, version);
            }
            
            helloMessage.ListenPort = decoded.GetInt(3);
            helloMessage.NodeId = new PublicKey(decoded.GetBytes(4));
            return helloMessage;
        }
    }
}