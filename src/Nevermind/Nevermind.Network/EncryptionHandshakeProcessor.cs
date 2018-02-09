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
using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class EncryptionHandshakeProcessor : MessageProcessorBase<byte[], Packet>
    {
        private readonly EncryptionHandshake _handshake;
        private readonly IMessageProcessingPipeline _pipeline;
        private readonly IEncryptionHandshakeService _service;
        private bool _isInitalized;

        public EncryptionHandshakeProcessor(
            IMessageProcessingPipeline pipeline,
            IEncryptionHandshakeService service,
            EncryptionHandshake handshake)
        {
            _pipeline = pipeline;
            _service = service;
            _handshake = handshake;
        }

        // TODO: can remote public key be a property of the pipeline / channel
        // TODO: replace pipeline with the channel?
        // TODO: can initialized by a property of the handshake?
        public void Initiate(PublicKey publicKey)
        {
            Packet authPacket = _service.Auth(publicKey, _handshake);
            _pipeline.Publish(authPacket);
            _isInitalized = true;
        }

        public override void ToRight(byte[] input, IList<Packet> output)
        {
            Packet incoming = new Packet(input);
            if (!_isInitalized)
            {
                _isInitalized = true;
                Packet ack = _service.Ack(_handshake, incoming);
                _pipeline.Publish(ack);
            }
            else
            {
                _service.Agree(_handshake, incoming);
            }
        }

        public override void ToLeft(Packet input, IList<byte[]> output)
        {
            output.Add(input.Data);
        }
    }
}