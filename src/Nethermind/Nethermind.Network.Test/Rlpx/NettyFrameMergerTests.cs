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
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class NettyFrameMergerTests
    {
        private class UnderTest : NettyFrameMerger
        {
            public UnderTest()
                : base(Substitute.For<ILogger>())
            {
            }

            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Decode(byte[] message, List<object> output)
            {
                base.Decode(_context, message, output);
            }
        }

        private class TestFrameHelper : NettyPacketSplitter
        {
            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Encode(Packet message, List<object> output)
            {
                base.Encode(_context, message, output);
            }
        }

        private static List<object> BuildFrames(int count)
        {
            TestFrameHelper frameBuilder = new TestFrameHelper();
            Packet packet = new Packet("eth", 2, new byte[(count - 1) * NettyPacketSplitter.FrameBoundary * 64 + 1]);
            List<object> frames = new List<object>();
            frameBuilder.Encode(packet, frames);
            return frames;
        }

        [Test]
        public void Handles_non_chunked_frames()
        {
            object frame = BuildFrames(1).Single();

            List<object> output = new List<object>();
            UnderTest underTest = new UnderTest();
            underTest.Decode((byte[])frame, output);

            Assert.AreEqual(1, output.Count);
        }

        [Test]
        public void Merges_frames_with_same_context_id()
        {
            List<object> frames = BuildFrames(3);

            List<object> output = new List<object>();
            UnderTest underTest = new UnderTest();
            for (int i = 0; i < frames.Count; i++)
            {
                underTest.Decode((byte[])frames[i], output);
                if (i < frames.Count - 1)
                {
                    Assert.AreEqual(0, output.Count);
                }
            }

            Assert.AreEqual(1, output.Count);
        }

        [Test]
        public void Sets_data_on_non_chunked_packets()
        {
            object frame = BuildFrames(1).Single();

            List<object> output = new List<object>();
            UnderTest underTest = new UnderTest();
            underTest.Decode((byte[])frame, output);

            Assert.AreEqual(1, ((Packet)output[0]).Data.Length); // TODO: check padding
        }
        
        [Test]
        public void Sets_data_on_chunked_packets()
        {
            List<object> frames = BuildFrames(3);
            
            List<object> output = new List<object>();
            UnderTest underTest = new UnderTest();
            for (int i = 0; i < frames.Count; i++)
            {
                underTest.Decode((byte[])frames[i], output);
            }

            Assert.AreEqual(2049, ((Packet)output[0]).Data.Length); // TODO: check padding
        }

        [Test]
        public void Sets_protocol_type_on_non_chunked_packets()
        {
            object frame = BuildFrames(1).Single();

            List<object> output = new List<object>();
            UnderTest underTest = new UnderTest();
            underTest.Decode((byte[])frame, output);

            Assert.AreEqual("???", ((Packet)output[0]).Protocol);
        }

        [Test]
        public void Sets_packet_type_on_non_chunked_packets()
        {
            object frame = BuildFrames(1).Single();

            List<object> output = new List<object>();
            UnderTest underTest = new UnderTest();
            underTest.Decode((byte[])frame, output);

            Assert.AreEqual(2, ((Packet)output[0]).PacketType);
        }
        
        [Test]
        public void Can_decode_neth_message()
        {
            byte[] frame = Bytes.FromHexString("0000adc18000000000000000000000000000000000000000000000000000000080f8aa05b8554e65746865726d696e642f76312e302e302d726332386465762d63396435353432612f5836342d4d6963726f736f66742057696e646f77732031302e302e3137313334202f436f7265342e362e32373631372e3035ccc5836574683ec5836574683f82765fb840824fa845597b92f99482f0d53993bf2562f8cf38e5ccb85ee4bb333df5cc51d197dc02fd0a533b3dfb6bad3f19aed405d68b72e413f8b206ae4ae31349fc7c1e00000000000000000000000000000000000000");

            List<object> output = new List<object>();
            UnderTest underTest = new UnderTest();
            underTest.Decode(frame, output);

            Packet packet = (Packet) output[0];
            HelloMessageSerializer serializer = new HelloMessageSerializer();
            HelloMessage helloMessage = serializer.Deserialize(packet.Data);

            Assert.AreEqual("Nethermind/v1.0.0-rc28dev-c9d5542a/X64-Microsoft Windows 10.0.17134 /Core4.6.27617.05", helloMessage.ClientId);
        }
    }
}