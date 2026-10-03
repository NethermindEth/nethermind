// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeMixedTransportTests
{
    [Test]
    public async Task Bulk_frame_backpressure_preserves_eth_response_and_session_control_over_tcp()
    {
        byte[] payload = new byte[LeanProofStore.MaxWrapperBytes];
        new Random(79124).NextBytes(payload);
        byte[] aes = new byte[32];
        byte[] mac = new byte[32];
        Array.Fill(mac, (byte)13);
        KeccakHash initial = KeccakHash.Create();
        initial.Update("mixed transport test"u8);
        EncryptionSecrets Secrets() => new()
        {
            AesSecret = aes,
            MacSecret = mac,
            EgressMac = initial.Copy(),
            IngressMac = initial.Copy()
        };
        using FrameMacProcessor outboundMac = new(TestItem.IgnoredPublicKey, Secrets());
        using FrameMacProcessor inboundMac = new(TestItem.IgnoredPublicKey, Secrets());
        ZeroPacketSplitter splitter = new(new FrameCipher(aes), outboundMac);
        splitter.EnableSnappy(LimboLogs.Instance);
        EmbeddedChannel outbound = new(splitter);
        LeanProofWrapperMessageSerializer leanSerializer = new();
        BlockHeadersMessageSerializer headersSerializer = new();
        MessageSerializationService serializers = new(
            SerializerInfo.Create(leanSerializer), SerializerInfo.Create(headersSerializer),
            SerializerInfo.Create(new PingMessageSerializer()));
        ISession session = Substitute.For<ISession>();
        List<int> delivered = [];
        TaskCompletionSource complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            delivered.Add(packet.PacketType);
            if (packet.PacketType == 1)
                Assert.That(MemoryExtensions.SequenceEqual<byte>(leanSerializer.Deserialize(packet.Content).Wrapper, payload), Is.True);
            else if (packet.PacketType == 20)
            {
                using BlockHeadersMessage decoded = headersSerializer.Deserialize(packet.Content);
                Assert.That(decoded.BlockHeaders!.Count, Is.EqualTo(1));
                Assert.That(decoded.BlockHeaders[0].Number, Is.EqualTo(42));
            }
            if (delivered.Count == 3) complete.TrySetResult();
        });
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        EmbeddedChannel inbound = new(new ZeroFrameDecoder(new FrameCipher(aes), inboundMac),
            new ZeroFrameMerger(LimboLogs.Instance), handler);
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        IChannel channel = Substitute.For<IChannel>();
        bool writable = true;
        channel.Active.Returns(true);
        channel.IsWritable.Returns(_ => writable);
        context.Channel.Returns(channel);
        context.Allocator.Returns(UnpooledByteBufferAllocator.Default);
        Queue<IByteBuffer> frames = [];
        context.WriteAndFlushAsync(Arg.Any<IByteBuffer>()).Returns(call =>
        {
            outbound.WriteOutbound(call.Arg<IByteBuffer>());
            frames.Enqueue(outbound.ReadOutbound<IByteBuffer>());
            return Task.CompletedTask;
        });
        PacketSender sender = new(serializers, LimboLogs.Instance, TimeSpan.Zero);
        sender.HandlerAdded(context);
        sender.EnableLeanBulk();
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using Socket transmit = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        transmit.Connect(listener.LocalEndPoint!);
        using Socket receive = listener.Accept();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            Assert.That(sender.Enqueue(new LeanProofWrapperMessage(payload) { AdaptivePacketType = 1 }), Is.GreaterThan(0));
            writable = false; // Bulk frame is outstanding at the channel's transport boundary.
            using BlockHeadersMessage headers = new(new ArrayPoolList<BlockHeader>(1) { Build.A.BlockHeader.WithNumber(42).TestObject })
            { AdaptivePacketType = 20 };
            PingMessage ping = PingMessage.Instance;
            ping.AdaptivePacketType = 2;
            long started = Stopwatch.GetTimestamp();
            Assert.That(sender.Enqueue(headers), Is.GreaterThan(0));
            Assert.That(sender.Enqueue(ping), Is.GreaterThan(0));
            Assert.That(sender.Enqueue(new LeanProofWrapperMessage(payload) { AdaptivePacketType = 1 }), Is.Zero);
            Assert.That(frames.Count, Is.EqualTo(1));
            Task reader = ReadFrames();
            await WriteFrame(frames.Dequeue());
            writable = true;
            sender.ChannelWritabilityChanged(context);
            Assert.That(frames.Count, Is.EqualTo(2));
            while (frames.TryDequeue(out IByteBuffer? frame)) await WriteFrame(frame);
            await reader;
            Assert.That(delivered, Is.EqualTo(new[] { 1, 20, 2 }));
            session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            TestContext.Out.WriteLine($"10 MiB bulk + ETH header + ping delivered in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms; deterministic writability transition, actual localhost TCP.");
        }
        finally
        {
            sender.HandlerRemoved(context);
            while (frames.TryDequeue(out IByteBuffer? frame)) frame.Release();
            outbound.FinishAndReleaseAll();
            inbound.FinishAndReleaseAll();
        }

        async Task WriteFrame(IByteBuffer frame)
        {
            try
            {
                int offset = 0;
                while (offset < frame.ReadableBytes)
                    offset += await transmit.SendAsync(frame.Array.AsMemory(frame.ArrayOffset + frame.ReaderIndex + offset,
                        frame.ReadableBytes - offset), SocketFlags.None, timeout.Token);
            }
            finally { frame.Release(); }
        }

        async Task ReadFrames()
        {
            while (!complete.Task.IsCompleted)
            {
                IByteBuffer? buffer = Unpooled.Buffer(64 * 1024);
                try
                {
                    int count = await receive.ReceiveAsync(buffer.Array.AsMemory(buffer.ArrayOffset, 64 * 1024), SocketFlags.None, timeout.Token);
                    if (count == 0) throw new EndOfStreamException("TCP peer closed");
                    buffer.SetWriterIndex(count);
                    inbound.WriteInbound(buffer);
                    buffer = null;
                }
                finally { buffer?.Release(); }
            }
        }
    }
}
