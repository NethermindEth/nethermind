// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

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
        (EncryptionSecrets A, EncryptionSecrets B) = NetTestVectors.GetSecretsPair();

        _frameCipherA = new FrameCipher(A.AesSecret);
        _macProcessorA = new FrameMacProcessor(TestItem.IgnoredPublicKey, A);

        _frameCipherB = new FrameCipher(B.AesSecret);
        _macProcessorB = new FrameMacProcessor(TestItem.IgnoredPublicKey, B);

        _frame = new byte[16 + 16 + 16 + 16];
        _frame[2] = 16; // size
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
                embeddedChannel.WriteOutbound(packetBuffer);  // packetBuffer is released here
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
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(decodedPacket.PacketType, Is.EqualTo(packet.PacketType));
                    Assert.That(decodedPacket.Content.ReadAllHex(), Is.EqualTo(packet.Data.ToHexString()));
                }
                decodedPacket.Release();
            }
            else // allocating
            {
                Packet decodedPacket = embeddedChannel.ReadInbound<Packet>();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(decodedPacket.PacketType, Is.EqualTo(packet.PacketType));
                    Assert.That(decodedPacket.Data.ToHexString(), Is.EqualTo(packet.Data.ToHexString()));
                }
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
        ZeroPacketSplitter splitter = new(_frameCipherA, _macProcessorA);

        if (!framingEnabled)
        {
            splitter.DisableFraming();
        }

        EmbeddedChannel embeddedChannel = new();
        embeddedChannel.Pipeline.AddLast(decoder);
        embeddedChannel.Pipeline.AddLast(merger);
        embeddedChannel.Pipeline.AddLast(splitter);

        return embeddedChannel;
    }

    public enum StackType
    {
        Zero
    }
}
