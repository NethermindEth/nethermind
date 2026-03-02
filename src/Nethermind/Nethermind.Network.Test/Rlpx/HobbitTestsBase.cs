// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Common.Internal.Logging;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Network.Test.Rlpx;

public abstract class HobbitTestsBase
{
    private byte[] _frame;

    private IFrameCipher _frameCipherA;
    private FrameMacProcessor _macProcessorA;

    private IFrameCipher _frameCipherB;
    private FrameMacProcessor _macProcessorB;

    [SetUp]
    public void Setup()
    {
        var (A, B) = NetTestVectors.GetSecretsPair();

        _frameCipherA = new FrameCipher(A.AesSecret);
        _macProcessorA = new FrameMacProcessor(TestItem.IgnoredPublicKey, A);

        _frameCipherB = new FrameCipher(B.AesSecret);
        _macProcessorB = new FrameMacProcessor(TestItem.IgnoredPublicKey, B);

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

    [TearDown]
    public void TearDown()
    {
        _macProcessorA?.Dispose();
        _macProcessorB?.Dispose();
    }

    protected Packet Run(Packet packet, StackType inbound, StackType outbound, bool framingEnabled)
    {
        EmbeddedChannel embeddedChannel = null;
        try
        {
            embeddedChannel = BuildEmbeddedChannel(inbound, outbound, framingEnabled);

            if (outbound == StackType.Zero)
            {
                byte[] rlpPacketType = Rlp.Encode((long)packet.PacketType).Bytes;
                IByteBuffer packetBuffer = embeddedChannel.Allocator.Buffer(rlpPacketType.Length + packet.Data.Length);
                packetBuffer.WriteBytes(rlpPacketType);
                packetBuffer.WriteBytes(packet.Data);
                embeddedChannel.WriteOutbound(packetBuffer);
            }
            else // allocating
            {
                embeddedChannel.WriteOutbound(packet);
            }

            while (embeddedChannel.OutboundMessages.Count != 0)
            {
                IByteBuffer encodedPacket = embeddedChannel.ReadOutbound<IByteBuffer>();
                embeddedChannel.WriteInbound(encodedPacket);
            }

            if (inbound == StackType.Zero)
            {
                ZeroPacket decodedPacket = embeddedChannel.ReadInbound<ZeroPacket>();
                Assert.That(decodedPacket.Content.ReadAllHex(), Is.EqualTo(packet.Data.ToHexString()));
                Assert.That(decodedPacket.PacketType, Is.EqualTo(packet.PacketType));
                decodedPacket.Release();
            }
            else // allocating
            {
                Packet decodedPacket = embeddedChannel.ReadInbound<Packet>();
                Assert.That(decodedPacket.PacketType, Is.EqualTo(packet.PacketType));
                Assert.That(decodedPacket.Data.ToHexString(), Is.EqualTo(packet.Data.ToHexString()));
                Assert.That(decodedPacket.PacketType, Is.EqualTo(packet.PacketType));
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

        IChannelHandler decoder = new ZeroFrameDecoder(_frameCipherB, _macProcessorB);
        IChannelHandler merger = new ZeroFrameMerger(LimboLogs.Instance);
        IChannelHandler encoder = new ZeroFrameEncoder(_frameCipherA, _macProcessorA);
        IFramingAware splitter = new ZeroPacketSplitter();

        Assert.That(splitter.MaxFrameSize, Is.EqualTo(Frame.DefaultMaxFrameSize), "default max frame size");

        if (!framingEnabled)
        {
            splitter.DisableFraming();
            Assert.That(splitter.MaxFrameSize, Is.EqualTo(int.MaxValue), "max frame size when framing disabled");
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

        public ConsoleLoggerOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ConsoleLoggerOptions, string> listener) => new Empty();

        private class Empty : IDisposable
        {
            public void Dispose() { }
        }
    }
}
