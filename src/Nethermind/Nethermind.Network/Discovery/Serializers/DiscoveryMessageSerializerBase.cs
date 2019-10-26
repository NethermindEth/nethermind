﻿/*
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
using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Messages;
using NLog;

namespace Nethermind.Network.Discovery.Serializers
{
    public abstract class DiscoveryMessageSerializerBase
    {
        private readonly PrivateKey _privateKey;
        private readonly IEcdsa _ecdsa;

        private readonly IDiscoveryMessageFactory _messageFactory;
        private readonly INodeIdResolver _nodeIdResolver;

        protected DiscoveryMessageSerializerBase(
            IEcdsa ecdsa,
            IPrivateKeyGenerator privateKeyGenerator,
            IDiscoveryMessageFactory messageFactory,
            INodeIdResolver nodeIdResolver)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _privateKey = privateKeyGenerator.Generate();
            _messageFactory = messageFactory ?? throw new ArgumentNullException(nameof(messageFactory));
            _nodeIdResolver = nodeIdResolver ?? throw new ArgumentNullException(nameof(nodeIdResolver));
        }

        protected byte[] Serialize(byte type, Span<byte> data)
        {
            Span<byte> result = new byte[32 + 1 + data.Length + 64 + 1].AsSpan();
            result[32 + 65] = type;
            data.CopyTo(result.Slice(32 + 65 + 1, data.Length));

            Span<byte> payload = result.Slice(32 + 65);
            Keccak toSign = Keccak.Compute(payload);
            Signature signature = _ecdsa.Sign(_privateKey, toSign);
            signature.Bytes.AsSpan().CopyTo(result.Slice(32, 64));
            result[32 + 64] = signature.RecoveryId;
            
            Span<byte> forMdc = result.Slice(32);
            Keccak mdc = Keccak.Compute(forMdc);
            mdc.Bytes.AsSpan().CopyTo(result.Slice(0,32));
            return result.ToArray();
        }

        protected (T Message, byte[] Mdc, byte[] Data) PrepareForDeserialization<T>(byte[] msg) where T : DiscoveryMessage
        {
            if (msg.Length < 98)
            {
                throw new NetworkingException("Incorrect message", NetworkExceptionType.Validation);
            }

            var mdc = msg.Slice(0, 32);
            var signature = msg.Slice(32, 65);
            // var type = new[] { msg[97] };
            var data = msg.Slice(98, msg.Length - 98);
            var computedMdc = Keccak.Compute(msg.Slice(32)).Bytes;

            if (!Bytes.AreEqual(mdc, computedMdc))
            {
                throw new NetworkingException("Invalid MDC", NetworkExceptionType.Validation);
            }

            var nodeId = _nodeIdResolver.GetNodeId(signature.Slice(0, 64), signature[64], msg.Slice(97, msg.Length - 97));
            var message = _messageFactory.CreateIncomingMessage<T>(nodeId);
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

        protected static IPEndPoint GetAddress(byte[] ip, int port)
        {
            IPAddress ipAddress;
            try
            {
                ipAddress = new IPAddress(ip);
            }
            catch (Exception)
            {
                ipAddress = IPAddress.Any;
            }

            return new IPEndPoint(ipAddress, port);
        }
    }
}