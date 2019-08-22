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
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class HobbitTests
    {
        [SetUp]
        public void Setup()
        {
            var secrets = NetTestVectors.GetSecretsPair();

            _frameCipherA = new FrameCipher(secrets.A.AesSecret);
            _macProcessorA = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.A);

            _frameCipherB = new FrameCipher(secrets.B.AesSecret);
            _macProcessorB = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.B);

            _frame = new byte[16 + 16 + 16 + 16];
            _frame[2] = 16; // size   
        }

        private byte[] _frame;

        private IFrameCipher _frameCipherA;
        private IFrameMacProcessor _macProcessorA;

        private IFrameCipher _frameCipherB;
        private IFrameMacProcessor _macProcessorB;

        private class FrameDecoderTest : NettyFrameDecoder
        {
            private readonly IChannelHandlerContext _context;

            public FrameDecoderTest(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, Substitute.For<ILogger>())
            {
                _context = Substitute.For<IChannelHandlerContext>();
            }

            public List<byte[]> Decode(IByteBuffer buffer)
            {
                List<object> result = new List<object>();
                base.Decode(_context, buffer, result);
                return result.Cast<byte[]>().ToList();
            }
        }

        private class FrameEncoderTest : NettyFrameEncoder
        {
            private readonly IChannelHandlerContext _context;

            public FrameEncoderTest(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor, NullLogger.Instance)
            {
                _context = Substitute.For<IChannelHandlerContext>();
            }

            public void Encode(byte[] message, IByteBuffer buffer)
            {
                base.Encode(_context, message, buffer);
            }
        }

        private class PacketSplitterTest : NettyPacketSplitter
        {
            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Encode(Packet message, List<object> output)
            {
                base.Encode(_context, message, output);
            }
        }

        private class FrameMerger : NettyFrameMerger
        {
            public FrameMerger()
                : base(Substitute.For<ILogger>())
            {
            }

            private readonly IChannelHandlerContext _context = Substitute.For<IChannelHandlerContext>();

            public void Decode(byte[] message, List<object> output)
            {
                base.Decode(_context, message, output);
            }
        }

        [Test]
        public void Block_there_and_back()
        {
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            Block block = Build.A.Block.WithTransactions(a, b).TestObject;
            NewBlockMessage newBlockMessage = new NewBlockMessage();
            newBlockMessage.Block = block;

            NewBlockMessageSerializer newBlockMessageSerializer = new NewBlockMessageSerializer();
            byte[] data = newBlockMessageSerializer.Serialize(newBlockMessage);
            Packet packet = new Packet("eth", 7, data);

            RunHobbitTest(packet);
        }

        [Test]
        public void Two_frame_block_there_and_back()
        {
            Transaction[] txs = Build.A.Transaction.SignedAndResolved().TestObjectNTimes(10);
            Block block = Build.A.Block.WithTransactions(txs).TestObject;
            NewBlockMessage newBlockMessage = new NewBlockMessage();
            newBlockMessage.Block = block;

            NewBlockMessageSerializer newBlockMessageSerializer = new NewBlockMessageSerializer();
            byte[] data = newBlockMessageSerializer.Serialize(newBlockMessage);
            Packet packet = new Packet("eth", 7, data);

            Packet decoded = RunHobbitTest(packet);
            NewBlockMessage decodedMessage = newBlockMessageSerializer.Deserialize(decoded.Data);
            Assert.AreEqual(newBlockMessage.Block.Transactions.Length, decodedMessage.Block.Transactions.Length);
        }

        private Packet RunHobbitTest(Packet packet)
        {
            IByteBuffer hobbitBuffer = PooledByteBufferAllocator.Default.Buffer();

            /***** THERE *****/

            List<object> output = new List<object>();

            PacketSplitterTest packetSplitter = new PacketSplitterTest();
            packetSplitter.Encode(packet, output);

            FrameEncoderTest frameEncoder = new FrameEncoderTest(_frameCipherA, _macProcessorA);
            foreach (byte[] frame in output.Cast<byte[]>())
            {
                frameEncoder.Encode(frame, hobbitBuffer);
                TestContext.Out.WriteLine("encoded frame: " + frame.ToHexString());
            }

            /***** AND BACK AGAIN *****/

            FrameDecoderTest frameDecoder = new FrameDecoderTest(_frameCipherB, _macProcessorB);
            List<byte[]> decodedFrames = new List<byte[]>();
            foreach (var _ in output)
            {
                decodedFrames.AddRange(frameDecoder.Decode(hobbitBuffer));
            }

            FrameMerger frameMerger = new FrameMerger();
            List<object> mergerResult = new List<object>();
            foreach (byte[] frame in decodedFrames)
            {
                TestContext.Out.WriteLine("decoded frame: " + frame.ToHexString());
                frameMerger.Decode(frame, mergerResult);
            }

            Packet decodedPacket = ((Packet) mergerResult[0]);

            Assert.AreEqual(packet.Data.ToHexString(), decodedPacket.Data.ToHexString());
            Assert.AreEqual(packet.PacketType, decodedPacket.PacketType);

            return packet;
        }
    }
}