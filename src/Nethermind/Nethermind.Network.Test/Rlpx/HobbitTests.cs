// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Common.Internal.Logging;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class HobbitTests
    {
        private byte[] _frame;

        private IFrameCipher _frameCipherA;
        private FrameMacProcessor _macProcessorA;

        private IFrameCipher _frameCipherB;
        private FrameMacProcessor _macProcessorB;

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

            InternalLoggerFactory.DefaultFactory.AddProvider(new ConsoleLoggerProvider(new ConsoleLoggerOptionsMonitor(
                new ConsoleLoggerOptions
                {
                    FormatterName = ConsoleFormatterNames.Simple,
                    LogToStandardErrorThreshold = LogLevel.Trace
                })));
            ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Paranoid;
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Get_block_bodies_there_and_back(StackType inbound, StackType outbound, bool framingEnabled)
        {
            var hashes = new Keccak[256];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = Keccak.Compute(i.ToString());
            }

            GetBlockBodiesMessage message = new(hashes);

            GetBlockBodiesMessageSerializer serializer = new();
            byte[] data = serializer.Serialize(message);

            Packet packet = new("eth", 5, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Block_there_and_back(StackType inbound, StackType outbound, bool framingEnabled)
        {
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            Block block = Build.A.Block.WithTransactions(a, b).TestObject;
            NewBlockMessage newBlockMessage = new();
            newBlockMessage.Block = block;

            NewBlockMessageSerializer newBlockMessageSerializer = new();
            byte[] data = newBlockMessageSerializer.Serialize(newBlockMessage);
            Packet packet = new("eth", 7, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Two_frame_block_there_and_back(StackType inbound, StackType outbound, bool framingEnabled)
        {
            Transaction[] txs = Build.A.Transaction.SignedAndResolved().TestObjectNTimes(10);
            Block block = Build.A.Block.WithTransactions(txs).TestObject;
            NewBlockMessage newBlockMessage = new();
            newBlockMessage.Block = block;

            NewBlockMessageSerializer newBlockMessageSerializer = new();
            byte[] data = newBlockMessageSerializer.Serialize(newBlockMessage);
            Packet packet = new("eth", 7, data);

            Packet decoded = Run(packet, inbound, outbound, framingEnabled);

            NewBlockMessage decodedMessage = newBlockMessageSerializer.Deserialize(decoded.Data);
            Assert.AreEqual(newBlockMessage.Block.Transactions.Length, decodedMessage.Block.Transactions.Length);
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Receipts_message(StackType inbound, StackType outbound, bool framingEnabled)
        {
            Keccak[] hashes = new Keccak[256];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = Keccak.Compute(i.ToString());
            }

            GetReceiptsMessage message = new(hashes);

            GetReceiptsMessageSerializer serializer = new();
            byte[] data = serializer.Serialize(message);
            Packet packet = new("eth", 7, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);

            GetReceiptsMessage decodedMessage = serializer.Deserialize(decoded.Data);
            Assert.AreEqual(message.Hashes.Count, decodedMessage.Hashes.Count);
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Status_message(StackType inbound, StackType outbound, bool framingEnabled)
        {
            StatusMessage message = new();
            message.BestHash = Keccak.Zero;
            message.GenesisHash = Keccak.Zero;
            message.ProtocolVersion = 63;
            message.TotalDifficulty = 10000000000;
            message.NetworkId = 5;

            StatusMessageSerializer serializer = new();
            byte[] data = serializer.Serialize(message);
            Packet packet = new("eth", 7, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);

            StatusMessage decodedMessage = serializer.Deserialize(decoded.Data);
            Assert.AreEqual(message.TotalDifficulty, decodedMessage.TotalDifficulty);
        }

        private Packet Run(Packet packet, StackType inbound, StackType outbound, bool framingEnabled)
        {
            EmbeddedChannel embeddedChannel = null;
            try
            {
                embeddedChannel = BuildEmbeddedChannel(inbound, outbound, framingEnabled);

                if (outbound == StackType.Zero)
                {
                    IByteBuffer packetBuffer = embeddedChannel.Allocator.Buffer(1 + packet.Data.Length);
                    packetBuffer.WriteByte(packet.PacketType);
                    packetBuffer.WriteBytes(packet.Data);
                    embeddedChannel.WriteOutbound(packetBuffer);
                }
                else // allocating
                {
                    embeddedChannel.WriteOutbound(packet);
                }

                while (embeddedChannel.OutboundMessages.Any())
                {
                    IByteBuffer encodedPacket = embeddedChannel.ReadOutbound<IByteBuffer>();
                    embeddedChannel.WriteInbound(encodedPacket);
                }

                if (inbound == StackType.Zero)
                {
                    ZeroPacket decodedPacket = embeddedChannel.ReadInbound<ZeroPacket>();
                    Assert.AreEqual(packet.Data.ToHexString(), decodedPacket.Content.ReadAllHex());
                    Assert.AreEqual(packet.PacketType, decodedPacket.PacketType);
                    decodedPacket.Release();
                }
                else // allocating
                {
                    Packet decodedPacket = embeddedChannel.ReadInbound<Packet>();
                    Assert.AreEqual(packet.Data.ToHexString(), decodedPacket.Data.ToHexString());
                    Assert.AreEqual(packet.PacketType, decodedPacket.PacketType);
                }
            }
            finally
            {
                embeddedChannel?.Finish();
            }

            return packet;
        }

        private EmbeddedChannel BuildEmbeddedChannel(StackType inbound, StackType outbound, bool framingEnabled = true)
        {
            if (inbound != StackType.Zero ||
                outbound != StackType.Zero)
            {
                throw new NotSupportedException();
            }

            IChannelHandler decoder = new ZeroFrameDecoder(_frameCipherB, _macProcessorB, LimboLogs.Instance);
            IChannelHandler merger = new ZeroFrameMerger(LimboLogs.Instance);
            IChannelHandler encoder = new ZeroFrameEncoder(_frameCipherA, _macProcessorA, LimboLogs.Instance);
            IFramingAware splitter = new ZeroPacketSplitter(LimboLogs.Instance);

            Assert.AreEqual(Frame.DefaultMaxFrameSize, splitter.MaxFrameSize, "default max frame size");

            if (!framingEnabled)
            {
                splitter.DisableFraming();
                Assert.AreEqual(int.MaxValue, splitter.MaxFrameSize, "max frame size when framing disabled");
            }

            EmbeddedChannel embeddedChannel = new();
            embeddedChannel.Pipeline.AddLast(decoder);
            embeddedChannel.Pipeline.AddLast(merger);
            embeddedChannel.Pipeline.AddLast(encoder);
            embeddedChannel.Pipeline.AddLast(splitter);

            return embeddedChannel;
        }

        public enum StackType
        {
            Zero
        }

        private class ConsoleLoggerOptionsMonitor : IOptionsMonitor<ConsoleLoggerOptions>
        {
            public ConsoleLoggerOptionsMonitor(ConsoleLoggerOptions currentValue)
            {
                CurrentValue = currentValue;
            }

            public ConsoleLoggerOptions CurrentValue { get; }

            public ConsoleLoggerOptions Get(string name) => CurrentValue;

            public IDisposable OnChange(Action<ConsoleLoggerOptions, string> listener)
            {
                return new Empty();
            }

            private class Empty : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }
}
