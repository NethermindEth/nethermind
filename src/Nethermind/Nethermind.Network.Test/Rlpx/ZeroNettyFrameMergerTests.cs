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

using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Rlpx.TestWrappers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class ZeroNettyFrameMergerTests
    {
        private class TestFrameHelper : ZeroPacketSplitter
        {
            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Encode(IByteBuffer input, IByteBuffer output)
            {
                base.Encode(_context, input, output);
            }

            public TestFrameHelper() : base(LimboLogs.Instance)
            {
            }
        }

        private static IByteBuffer BuildFrames(int count)
        {
            TestFrameHelper frameBuilder = new TestFrameHelper();
            int totalLength = (count - 1) * Frame.DefaultMaxFrameSize + 1;
            IByteBuffer input = PooledByteBufferAllocator.Default.Buffer(1 +  totalLength);
            input.WriteByte(2);
            input.WriteZero(totalLength);
            
            IByteBuffer output = PooledByteBufferAllocator.Default.Buffer(totalLength + Frame.CalculatePadding(totalLength) + count * 16);
            frameBuilder.Encode(input, output);
            return output;
        }

        [Test]
        public void Handles_non_chunked_frames()
        {
            IByteBuffer input = BuildFrames(1);
            ZeroFrameMergerTestWrapper zeroFrameMergerTestWrapper = new ZeroFrameMergerTestWrapper();
            var output = zeroFrameMergerTestWrapper.Decode(input);

            Assert.NotNull(output);
        }

        [Test]
        public void Merges_frames_with_same_context_id()
        {
            IByteBuffer input = BuildFrames(3);
            ZeroFrameMergerTestWrapper zeroFrameMergerTestWrapper = new ZeroFrameMergerTestWrapper();
            ZeroPacket output = zeroFrameMergerTestWrapper.Decode(input);
            Assert.NotNull(output);
        }

        [Test]
        public void Sets_data_on_non_chunked_packets()
        {
            IByteBuffer input = BuildFrames(1);
            
            ZeroFrameMergerTestWrapper zeroFrameMergerTestWrapper = new ZeroFrameMergerTestWrapper();
            ZeroPacket output = zeroFrameMergerTestWrapper.Decode(input);
            Assert.NotNull(output);
            Assert.AreEqual(1, output.Content.ReadableBytes);
        }

        [Test]
        public void Sets_data_on_chunked_packets()
        {
            IByteBuffer input = BuildFrames(3);
            
            ZeroFrameMergerTestWrapper zeroFrameMergerTestWrapper = new ZeroFrameMergerTestWrapper();
            ZeroPacket output = zeroFrameMergerTestWrapper.Decode(input);
            Assert.NotNull(output);
            Assert.AreEqual(2049, output.Content.ReadableBytes);
        }

        [Test]
        public void Sets_packet_type_on_non_chunked_packets()
        {
            IByteBuffer input = BuildFrames(1);

            ZeroFrameMergerTestWrapper zeroFrameMergerTestWrapper = new ZeroFrameMergerTestWrapper();
            ZeroPacket output = zeroFrameMergerTestWrapper.Decode(input);
            Assert.NotNull(output);
            Assert.AreEqual((byte)2, output.PacketType);
        }

        [Test]
        public void Can_decode_neth_message()
        {
            byte[] frame = Bytes.FromHexString("0000adc180000000000000000000000080f8aa05b8554e65746865726d696e642f76312e302e302d726332386465762d63396435353432612f5836342d4d6963726f736f66742057696e646f77732031302e302e3137313334202f436f7265342e362e32373631372e3035ccc5836574683ec5836574683f82765fb840824fa845597b92f99482f0d53993bf2562f8cf38e5ccb85ee4bb333df5cc51d197dc02fd0a533b3dfb6bad3f19aed405d68b72e413f8b206ae4ae31349fc7c1e000000");
            IByteBuffer input = PooledByteBufferAllocator.Default.Buffer();
            input.WriteBytes(frame);
            
            ZeroFrameMergerTestWrapper zeroFrameMergerTestWrapper = new ZeroFrameMergerTestWrapper();
            ZeroPacket output = zeroFrameMergerTestWrapper.Decode(input);
            Assert.NotNull(output);

            Assert.AreEqual(0, output.PacketType);
            
            byte[] outputBytes = output.Content.ReadAllBytes();
            HelloMessageSerializer serializer = new HelloMessageSerializer();
            HelloMessage helloMessage = serializer.Deserialize(outputBytes);

            Assert.AreEqual("Nethermind/v1.0.0-rc28dev-c9d5542a/X64-Microsoft Windows 10.0.17134 /Core4.6.27617.05", helloMessage.ClientId);
            Assert.AreEqual(input.ReaderIndex, input.WriterIndex, "reader index == writer index");
        }
    }
}