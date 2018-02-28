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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P
{
    public class HelloMessageSerializer : IMessageSerializer<HelloMessage>
    {
        public byte[] Serialize(HelloMessage message, IMessagePad pad = null)
        {
            return Rlp.Encode(
                Rlp.Encode(message.P2PVersion),
                Rlp.Encode(message.ClientId),
                Rlp.Encode(message.Capabilities.Select(c => Rlp.Encode(c.Key.ToString(), c.Value)).ToArray()),
                Rlp.Encode(message.ListenPort),
                Rlp.Encode(message.NodeId.PrefixedBytes)
            ).Bytes;
        }

        public HelloMessage Deserialize(byte[] bytes)
        {
            object[] decoded = (object[])Rlp.Decode(new Rlp(bytes));
            HelloMessage helloMessage = new HelloMessage();
            helloMessage.P2PVersion = ((byte[])decoded[0]).Length == 0 ? (byte)0 : ((byte[])decoded[0])[0]; // TODO: improve RLP decoding API
            helloMessage.ClientId = Encoding.UTF8.GetString((byte[])decoded[1]);
            helloMessage.Capabilities = new Dictionary<Capability, int>();
            object[] decodedCapabilities = (object[])decoded[2];
            for (int i = 0; i < decodedCapabilities.Length; i++)
            {
                byte[] nameBytes = (byte[])((object[])decodedCapabilities[i])[0];
                byte[] versionBytes = (byte[])((object[])decodedCapabilities[i])[1];
                string name = Encoding.UTF8.GetString(nameBytes);
                int version = versionBytes.Length == 0 ? 0 : versionBytes[0];
                helloMessage.Capabilities.Add((Capability)Enum.Parse(typeof(Capability), name), version);
            }
            // TODO: capabilities
            helloMessage.ListenPort = ((byte[])decoded[3]).Length == 0 ? 0 : ((byte[])decoded[3]).ToInt32();
            helloMessage.NodeId = new PublicKey((byte[])decoded[4]);
            return helloMessage;
        }
    }
}