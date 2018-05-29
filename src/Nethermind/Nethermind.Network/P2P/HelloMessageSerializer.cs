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
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P
{
    public class HelloMessageSerializer : IMessageSerializer<HelloMessage>
    {
        public byte[] Serialize(HelloMessage message)
        {
            return Rlp.Encode(
                Rlp.Encode(message.P2PVersion),
                Rlp.Encode(message.ClientId),
                Rlp.Encode(message.Capabilities.Select(c => Rlp.Encode(
                    Rlp.Encode(c.ProtocolCode.ToLowerInvariant()),
                    Rlp.Encode(c.Version))).ToArray()),
                Rlp.Encode(message.ListenPort),
                Rlp.Encode(message.NodeId.PrefixedBytes)
            ).Bytes;
        }

        public HelloMessage Deserialize(byte[] bytes)
        {
            Rlp.DecoderContext context = bytes.AsRlpContext();
            context.ReadSequenceLength();

            HelloMessage helloMessage = new HelloMessage();
            helloMessage.P2PVersion = context.DecodeByte();
            helloMessage.ClientId = context.DecodeString();
            helloMessage.Capabilities = context.DecodeArray(ctx =>
            {
                ctx.ReadSequenceLength();
                string protocolCode = ctx.DecodeString();
                int version = ctx.DecodeByte();
                return new Capability(protocolCode, version);
            }).ToList();
            
            helloMessage.ListenPort = context.DecodeInt();
            helloMessage.NodeId = new PublicKey(context.DecodeByteArray());
            return helloMessage;
        }
    }
}