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

using Nevermind.Core.Extensions;
using Nevermind.Network.P2P;
using Nevermind.Network.Rlpx;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Digests;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class FramingTests
    {
        private static EncryptionSecrets BuildSecrets()
        {
            EncryptionSecrets secrets = new EncryptionSecrets();
            secrets.AesSecret = NetTestVectors.AesSecret;
            secrets.MacSecret = NetTestVectors.MacSecret;

            byte[] bytes = NetTestVectors.AesSecret.Xor(NetTestVectors.MacSecret);

            KeccakDigest egressMac = new KeccakDigest(256);
            egressMac.BlockUpdate(bytes, 0, 32);
            secrets.EgressMac = egressMac;

            KeccakDigest ingressMac = new KeccakDigest(256);
            ingressMac.BlockUpdate(bytes, 0, 32);
            secrets.IngressMac = ingressMac;
            return secrets;
        }

        [Test]
        public void Size_looks_ok()
        {
            MessageProcessingPipeline pipeline = new MessageProcessingPipeline();
            pipeline.AddRight(new FrameEncryptionProcessor(BuildSecrets()));
            pipeline.AddRight(new FrameSplittingProcessor());

            byte[] output = pipeline.Publish(new Packet(1, 2, new byte[1]));
            Assert.AreEqual(16 + 16 + 1 + 16 + 16, output.Length);
        }

        [Test]
        public void Size_looks_ok_multiple_frames()
        {
            MessageProcessingPipeline pipeline = new MessageProcessingPipeline();
            pipeline.AddRight(new FrameEncryptionProcessor(BuildSecrets()));
            pipeline.AddRight(new FrameSplittingProcessor());

            byte[] output = pipeline.Publish(new Packet(1, 2, new byte[FrameSplittingProcessor.MaxFrameSize + 1]));
            Assert.AreEqual(16 + 16 + 1 /* packet type */ + 1024 + 16 /* frame boundary */ + 16 + 16 + 16 /* padded */ + 16, output.Length);
        }
    }
}