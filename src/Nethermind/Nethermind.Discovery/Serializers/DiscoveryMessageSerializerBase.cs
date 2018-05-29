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

using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;

namespace Nethermind.Discovery.Serializers
{
    public abstract class DiscoveryMessageSerializerBase
    {
        private readonly PrivateKey _privateKey;
        private readonly ISigner _signer;

        protected readonly IDiscoveryMessageFactory MessageFactory;
        protected readonly INodeIdResolver NodeIdResolver;
        protected readonly INodeFactory NodeFactory;

        protected DiscoveryMessageSerializerBase(ISigner signer, IPrivateKeyProvider privateKeyProvider, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory)
        {
            _signer = signer;
            _privateKey = privateKeyProvider.PrivateKey;
            MessageFactory = messageFactory;
            NodeIdResolver = nodeIdResolver;
            NodeFactory = nodeFactory;
        }

        protected byte[] Serialize(byte[] type, byte[] data)
        {
            byte[] payload = Bytes.Concat(type[0], data);
            Keccak toSign = Keccak.Compute(payload);
            Signature signature = _signer.Sign(_privateKey, toSign);
            byte[] signatureBytes = Bytes.Concat(signature.Bytes, signature.RecoveryId);
            byte[] mdc = Keccak.Compute(Bytes.Concat(signatureBytes, type, data)).Bytes;
            return Bytes.Concat(mdc, signatureBytes, type, data);
        }

        protected (T Message, byte[] Mdc, byte[] Data) PrepareForDeserialization<T>(byte[] msg) where T : DiscoveryMessage
        {
            if (msg.Length < 98)
            {
                throw new NetworkingException("Incorrect message");
            }

            var mdc = msg.Slice(0, 32);
            var signature = msg.Slice(32, 65);
            var type = new[] { msg[97] };
            var data = msg.Slice(98, msg.Length - 98);
            var computedMdc = Keccak.Compute(msg.Slice(32)).Bytes;

            if (!Bytes.UnsafeCompare(mdc, computedMdc))
            {
                throw new NetworkingException("Invalid MDC");
            }

            var nodeId = NodeIdResolver.GetNodeId(signature.Slice(0, 64), signature[64], type, data);
            var message = MessageFactory.CreateIncomingMessage<T>(nodeId);
            return (message, mdc, data);
        }

        protected Rlp Encode(IPEndPoint address)
        {
            return Rlp.Encode(
                Rlp.Encode(address.Address.GetAddressBytes()),
                //tcp port
                Rlp.Encode(address.Port),
                //udp port
                Rlp.Encode(address.Port)
            );
        }

        protected Rlp SerializeNode(IPEndPoint address, byte[] id)
        {
            return Rlp.Encode(
                Rlp.Encode(address.Address.GetAddressBytes()),
                //tcp port
                Rlp.Encode(address.Port),
                //udp port
                Rlp.Encode(address.Port),
                Rlp.Encode(id)
            );
        }

        protected IPEndPoint GetAddress(byte[] ip, int port)
        {
            return new IPEndPoint(new IPAddress(ip), port);
        }
    }
}