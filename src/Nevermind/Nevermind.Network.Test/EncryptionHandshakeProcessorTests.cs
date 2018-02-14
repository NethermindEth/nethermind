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
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using Nevermind.Network.Rlpx;
using Nevermind.Network.Rlpx.Handshake;
using NSubstitute;
using NUnit.Framework;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class EncryptionHandshakeProcessorTests
    {
        [Test]
        public void Can_ack()
        {
            IMessageProcessingPipeline pipeline = Substitute.For<IMessageProcessingPipeline>();
            IEncryptionHandshakeService handshakeService = Substitute.For<IEncryptionHandshakeService>();

            Packet mockPacket = new Packet(Bytes.Empty);
            handshakeService.Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>()).Returns(mockPacket);

            EncryptionHandshake handshake = new EncryptionHandshake();

            EncryptionHandshakeProcessor processor = new EncryptionHandshakeProcessor(pipeline, handshakeService, handshake);
            List<Packet> packets = new List<Packet>();
            processor.ToRight(NetTestVectors.AuthEip8, packets);

            handshakeService.Received(1).Ack(handshake, Arg.Is<Packet>(p => new Hex(p.Data).Equals(NetTestVectors.AuthEip8)));
            pipeline.Received(1).Publish(mockPacket);
        }

        [Test]
        public void Can_agree()
        {
            IMessageProcessingPipeline pipeline = Substitute.For<IMessageProcessingPipeline>();
            IEncryptionHandshakeService handshakeService = Substitute.For<IEncryptionHandshakeService>();

            Packet mockPacket = new Packet(Bytes.Empty);
            handshakeService.Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>()).Returns(mockPacket);

            EncryptionHandshake handshake = new EncryptionHandshake();

            EncryptionHandshakeProcessor processor = new EncryptionHandshakeProcessor(pipeline, handshakeService, handshake);
            List<Packet> packets = new List<Packet>();
            processor.Initiate(NetTestVectors.EphemeralKeyA.PublicKey);
            processor.ToRight(NetTestVectors.AckEip8, packets);

            handshakeService.Received(1).Agree(handshake, Arg.Is<Packet>(p => new Hex(p.Data).Equals(NetTestVectors.AckEip8)));
            pipeline.Received(1).Publish(Arg.Any<Packet>());
        }

        [Test]
        public void Can_initiate()
        {
            IMessageProcessingPipeline pipeline = Substitute.For<IMessageProcessingPipeline>();
            IEncryptionHandshakeService handshakeService = Substitute.For<IEncryptionHandshakeService>();
            EncryptionHandshake handshake = new EncryptionHandshake();
            EncryptionHandshakeProcessor processor = new EncryptionHandshakeProcessor(pipeline, handshakeService, handshake);

            Packet mockPacket = new Packet(Bytes.Empty);
            handshakeService.Auth(Arg.Any<PublicKey>(), Arg.Any<EncryptionHandshake>()).Returns(mockPacket);

            PublicKey publicKey = NetTestVectors.StaticKeyA.PublicKey;
            processor.Initiate(publicKey);

            handshakeService.Received(1).Auth(publicKey, handshake);
            pipeline.Received(1).Publish(mockPacket);
        }
    }
}