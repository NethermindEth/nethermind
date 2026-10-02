// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

[NonParallelizable]
public class LeanTransportTests
{
    [Test]
    public void Maximum_wrapper_roundtrips_through_compressed_encrypted_frames()
    {
        Assert.That(Snappy.GetMaxCompressedLength(LeanProofStore.MaxWrapperBytes)
            + Rlp.LengthOf(ulong.MaxValue) + Frame.HeaderSize + 2 * Frame.MacSize + Frame.BlockSize - 1,
            Is.LessThanOrEqualTo(ZeroFrameDecoder.DefaultMaxInboundFrameSize));
        byte[] payload = new byte[LeanProofStore.MaxWrapperBytes];
        new Random(17342).NextBytes(payload);
        LeanProofWrapperMessageSerializer serializer = new();
        (EncryptionSecrets a, EncryptionSecrets b) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor outboundMac = new(TestItem.IgnoredPublicKey, a);
        using FrameMacProcessor inboundMac = new(TestItem.IgnoredPublicKey, b);
        ZeroPacketSplitter splitter = new(new FrameCipher(a.AesSecret), outboundMac);
        splitter.EnableSnappy(LimboLogs.Instance);
        EmbeddedChannel outbound = new(splitter);
        ISession session = Substitute.For<ISession>();
        ZeroPacket received = null;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            received = call.Arg<ZeroPacket>();
            received.Retain();
        });
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        EmbeddedChannel inbound = new(new ZeroFrameDecoder(new FrameCipher(b.AesSecret), inboundMac),
            new ZeroFrameMerger(LimboLogs.Instance), handler);
        try
        {
            IByteBuffer input = Unpooled.Buffer(payload.Length + 1);
            input.WriteByte(1);
            serializer.Serialize(input, new LeanProofWrapperMessage(payload));
            outbound.WriteOutbound(input);
            IByteBuffer wire = outbound.ReadOutbound<IByteBuffer>();
            Assert.That(wire.ReadableBytes, Is.LessThan(ZeroFrameDecoder.DefaultMaxInboundFrameSize));
            inbound.WriteInbound(wire);
            Assert.That(received, Is.Not.Null);
            Assert.That(received.PacketType, Is.EqualTo(1));
            Assert.That(serializer.Deserialize(received.Content).Wrapper.AsSpan(), Is.SequenceEqualTo(payload));
            session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            received?.Release();
            outbound.FinishAndReleaseAll();
            inbound.FinishAndReleaseAll();
        }
    }

    [Test]
    public void Oversized_wrapper_is_rejected_before_copy_or_serialization()
    {
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(new byte[LeanProofStore.MaxWrapperBytes + 1]).AsDisposable();
        LeanProofWrapperMessageSerializer serializer = new();
        Assert.Throws<RlpException>(() => serializer.Deserialize(buffer));
        Assert.That(buffer.ReaderIndex, Is.Zero);
        buffer.Clear();
        Assert.Throws<ArgumentException>(() => serializer.Serialize(buffer,
            new LeanProofWrapperMessage(new byte[LeanProofStore.MaxWrapperBytes + 1])));
        Assert.That(buffer.WriterIndex, Is.Zero);
    }
}
