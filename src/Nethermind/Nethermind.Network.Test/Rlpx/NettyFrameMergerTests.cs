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
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class NettyFrameMergerTests
    {
        private class FrameMerger : NettyFrameMerger
        {
            public FrameMerger()
                : base(LimboLogs.Instance)
            {
            }

            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Decode(byte[] message, List<object> output)
            {
                base.Decode(_context, message, output);
            }
        }

        private class PacketSplitter : NettyPacketSplitter
        {
            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Encode(Packet message, List<object> output)
            {
                base.Encode(_context, message, output);
            }

            public PacketSplitter() : base(LimboLogs.Instance)
            {
            }
        }

        private static List<object> BuildFrames(int count)
        {
            PacketSplitter frameBuilder = new PacketSplitter();
            Packet packet = new Packet("eth", 2, new byte[(count - 1) * Frame.DefaultMaxFrameSize + 1]);
            List<object> frames = new List<object>();
            frameBuilder.Encode(packet, frames);
            return frames;
        }

        [Test]
        public void Handles_non_chunked_frames()
        {
            object frame = BuildFrames(1).Single();

            List<object> output = new List<object>();
            FrameMerger frameMerger = new FrameMerger();
            frameMerger.Decode((byte[])frame, output);

            Assert.AreEqual(1, output.Count);
        }

        [Test]
        public void Merges_frames_with_same_context_id()
        {
            List<object> frames = BuildFrames(3);

            List<object> output = new List<object>();
            FrameMerger frameMerger = new FrameMerger();
            for (int i = 0; i < frames.Count; i++)
            {
                frameMerger.Decode((byte[])frames[i], output);
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
            FrameMerger frameMerger = new FrameMerger();
            frameMerger.Decode((byte[])frame, output);

            Assert.AreEqual(1, ((Packet)output[0]).Data.Length); // TODO: check padding
        }
        
        [Test]
        public void Sets_data_on_chunked_packets()
        {
            List<object> frames = BuildFrames(3);
            
            List<object> output = new List<object>();
            FrameMerger frameMerger = new FrameMerger();
            for (int i = 0; i < frames.Count; i++)
            {
                frameMerger.Decode((byte[])frames[i], output);
            }

            Assert.AreEqual(2049, ((Packet)output[0]).Data.Length); // TODO: check padding
        }

        [Test]
        public void Sets_protocol_type_on_non_chunked_packets()
        {
            object frame = BuildFrames(1).Single();

            List<object> output = new List<object>();
            FrameMerger frameMerger = new FrameMerger();
            frameMerger.Decode((byte[])frame, output);

            Assert.AreEqual("???", ((Packet)output[0]).Protocol);
        }

        [Test]
        public void Sets_packet_type_on_non_chunked_packets()
        {
            object frame = BuildFrames(1).Single();

            List<object> output = new List<object>();
            FrameMerger frameMerger = new FrameMerger();
            frameMerger.Decode((byte[])frame, output);

            Assert.AreEqual(2, ((Packet)output[0]).PacketType);
        }

        [Test]
        public void Can_decode_blocks_message()
        {
            byte[] frame0 = Bytes.FromHexString("000400c580018205d200000000000000d28f76b794402bd97ba5a7dfa1b0e16307f905cef905caf901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8f903caf85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000016bdb36c022deba8d5329900da5c9a3b");
            byte[] frame1 = Bytes.FromHexString("0001d2c280010000000000000000000083542b3751199587e9a0a0cad9b7e53600000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5c08000000000000000000000000000004827ebd3af739e872b59e6566c928933");

            List<object> output = new List<object>();
            FrameMerger frameMerger = new FrameMerger();
            frameMerger.Decode(frame0, output);
            frameMerger.Decode(frame1, output);

            Packet packet = (Packet) output[0];
            NewBlockMessageSerializer serializer = new NewBlockMessageSerializer();
            NewBlockMessage helloMessage = serializer.Deserialize(packet.Data);

            Assert.AreEqual(10, helloMessage.Block.Transactions.Length);
        }
        
        [Test]
        public void Can_decode_neth_message()
        {
            byte[] frame = Bytes.FromHexString("0000adc18000000000000000000000000000000000000000000000000000000080f8aa05b8554e65746865726d696e642f76312e302e302d726332386465762d63396435353432612f5836342d4d6963726f736f66742057696e646f77732031302e302e3137313334202f436f7265342e362e32373631372e3035ccc5836574683ec5836574683f82765fb840824fa845597b92f99482f0d53993bf2562f8cf38e5ccb85ee4bb333df5cc51d197dc02fd0a533b3dfb6bad3f19aed405d68b72e413f8b206ae4ae31349fc7c1e00000000000000000000000000000000000000");

            List<object> output = new List<object>();
            FrameMerger frameMerger = new FrameMerger();
            frameMerger.Decode(frame, output);

            Packet packet = (Packet) output[0];
            HelloMessageSerializer serializer = new HelloMessageSerializer();
            HelloMessage helloMessage = serializer.Deserialize(packet.Data);

            Assert.AreEqual("Nethermind/v1.0.0-rc28dev-c9d5542a/X64-Microsoft Windows 10.0.17134 /Core4.6.27617.05", helloMessage.ClientId);
        }
    }
}