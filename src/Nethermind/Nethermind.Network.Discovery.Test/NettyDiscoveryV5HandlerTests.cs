// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NettyDiscoveryV5HandlerTests
    {
        private EmbeddedChannel _channel;
        private NettyDiscoveryV5Handler _handler;

        [SetUp]
        public void Initialize()
        {
            _channel = new();
            _handler = new(new TestLogManager());
            _handler.InitializeChannel(_channel);
        }

        [TearDown]
        public async Task CleanUp() => await _channel.CloseAsync();

        [Test]
        public async Task ForwardsSentMessageToChannel()
        {
            byte[] data = [1, 2, 3];
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");

            await _handler.SendAsync(data, to, CancellationToken.None);

            DatagramPacket packet = _channel.ReadOutbound<DatagramPacket>();
            try
            {
                Assert.That(packet, Is.Not.Null);
                Assert.That(packet.Content.ReadAllBytesAsArray(), Is.EqualTo(data));
                Assert.That(packet.Recipient, Is.EqualTo(to));
            }
            finally
            {
                ReferenceCountUtil.Release(packet);
            }
        }

        [Test]
        public void DoesNotSendWhenTokenIsAlreadyCanceled()
        {
            byte[] data = [1, 2, 3];
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");
            using CancellationTokenSource cancellationSource = new();
            cancellationSource.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(
                async () => await _handler.SendAsync(data, to, cancellationSource.Token));

            DatagramPacket? packet = _channel.ReadOutbound<DatagramPacket>();
            try
            {
                Assert.That(packet, Is.Null);
            }
            finally
            {
                ReferenceCountUtil.Release(packet);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddressNotAvailableSendFailureIsTraceOnly(bool traceEnabled)
        {
            TestLogger logger = new() { IsDebug = true, IsTrace = traceEnabled };
            IChannel channel = Substitute.For<IChannel>();
            channel.WriteAndFlushAsync(Arg.Any<object>())
                .Returns(Task.FromException(new SocketException((int)SocketError.AddressNotAvailable)));
            NettyDiscoveryV5Handler handler = new(new OneLoggerLogManager(new ILogger(logger)), channel);
            IPEndPoint destination = new(IPAddress.Parse("2001:db8::1"), 30303);

            Assert.ThrowsAsync<SocketException>(
                async () => await handler.SendAsync([1, 2, 3], destination, CancellationToken.None));

            if (traceEnabled)
                Assert.That(logger.LogList, Has.Some.EqualTo($"TRACE/ERROR: Failed to send discv5 UDP packet to {destination}"));
            else
                Assert.That(logger.LogList, Is.Empty);
        }

        [Test]
        public async Task ForwardsReceivedMessageToReader()
        {
            byte[] data = [1, 2, 3];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");

            using CancellationTokenSource cancellationSource = new(10_000);
            await using IAsyncEnumerator<PooledUdpReceiveResult> enumerator = _handler
                .ReadMessagesAsync(cancellationSource.Token)
                .GetAsyncEnumerator(cancellationSource.Token);
            ValueTask<bool> readTask = enumerator.MoveNextAsync();

            IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();

            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));

            Assert.That(await readTask, Is.True);
            PooledUdpReceiveResult forwardedPacket = enumerator.Current;

            try
            {
                Assert.That(forwardedPacket.Buffer.ToArray(), Is.EqualTo(data));
                Assert.That(forwardedPacket.RemoteEndPoint, Is.EqualTo(from));
            }
            finally
            {
                forwardedPacket.Dispose();
            }
        }

        [Test]
        [NonParallelizable]
        public async Task UpdatesDiscoveryBytesSentMetric()
        {
            byte[] sentData = [1, 2, 3, 4];
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");
            long bytesSentBefore = Interlocked.Read(ref Metrics.DiscoveryBytesSent);

            await _handler.SendAsync(sentData, to, CancellationToken.None);
            DatagramPacket outboundPacket = _channel.ReadOutbound<DatagramPacket>();
            try
            {
                Assert.That(outboundPacket, Is.Not.Null);
            }
            finally
            {
                ReferenceCountUtil.Release(outboundPacket);
            }

            Assert.That(Interlocked.Read(ref Metrics.DiscoveryBytesSent) - bytesSentBefore, Is.EqualTo(sentData.Length));
        }

        [Test]
        [NonParallelizable]
        public async Task CompositeProtocolPipelineCountsInboundPacketOnce()
        {
            byte[] data = new byte[100];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");
            long bytesReceivedBefore = Interlocked.Read(ref Metrics.DiscoveryBytesReceived);
            EmbeddedChannel channel = new();
            NettyDiscoveryV5Handler discv5Handler = new(new TestLogManager());
            discv5Handler.InitializeChannel(channel);
            channel.Pipeline.AddLast(new DiscoveryTrafficHandler());
            channel.Pipeline.AddLast(new NettyDiscoveryHandler(
                Substitute.For<IDiscoveryMsgListener>(),
                channel,
                Substitute.For<IMessageSerializationService>(),
                Substitute.For<ITimestamper>(),
                new TestLogManager()));
            channel.Pipeline.AddLast(discv5Handler);

            try
            {
                using CancellationTokenSource cancellationSource = new(10_000);
                await using IAsyncEnumerator<PooledUdpReceiveResult> enumerator = discv5Handler
                    .ReadMessagesAsync(cancellationSource.Token)
                    .GetAsyncEnumerator(cancellationSource.Token);
                ValueTask<bool> readTask = enumerator.MoveNextAsync();

                channel.WriteInbound(new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));

                Assert.That(await readTask, Is.True);
                enumerator.Current.Dispose();

                Assert.That(Interlocked.Read(ref Metrics.DiscoveryBytesReceived) - bytesReceivedBefore, Is.EqualTo(data.Length));
            }
            finally
            {
                channel.FinishAndReleaseAll();
            }
        }

        [Test]
        public async Task MapsIpv4MappedIpv6SenderToIpv4()
        {
            byte[] data = [1, 2, 3];
            IPEndPoint from = new(IPAddress.Parse("::ffff:127.0.0.1"), 10000);
            IPEndPoint expectedFrom = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");

            using CancellationTokenSource cancellationSource = new(10_000);
            await using IAsyncEnumerator<PooledUdpReceiveResult> enumerator = _handler
                .ReadMessagesAsync(cancellationSource.Token)
                .GetAsyncEnumerator(cancellationSource.Token);
            ValueTask<bool> readTask = enumerator.MoveNextAsync();

            IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();

            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));

            Assert.That(await readTask, Is.True);
            PooledUdpReceiveResult forwardedPacket = enumerator.Current;

            try
            {
                Assert.That(forwardedPacket.Buffer.ToArray(), Is.EqualTo(data));
                Assert.That(forwardedPacket.RemoteEndPoint, Is.EqualTo(expectedFrom));
            }
            finally
            {
                forwardedPacket.Dispose();
            }
        }

        [TestCase(0)]
        [TestCase(1280 + 1)]
        public async Task SkipsMessagesOfInvalidSize(int size)
        {
            byte[] data = [1, 2, 3];
            byte[] invalidData = Enumerable.Repeat((byte)1, size).ToArray();
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");

            using CancellationTokenSource cancellationSource = new(10_000);
            await using IAsyncEnumerator<PooledUdpReceiveResult> enumerator = _handler
                .ReadMessagesAsync(cancellationSource.Token)
                .GetAsyncEnumerator(cancellationSource.Token);
            ValueTask<bool> readTask = enumerator.MoveNextAsync();

            IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();

            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])invalidData.Clone()), from, to));
            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));
            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])invalidData.Clone()), from, to));
            _handler.Close();

            Assert.That(await readTask, Is.True);
            PooledUdpReceiveResult forwardedPacket = enumerator.Current;
            try
            {
                Assert.That(forwardedPacket.Buffer.ToArray(), Is.EqualTo(data));
            }
            finally
            {
                forwardedPacket.Dispose();
            }

            Assert.That(await enumerator.MoveNextAsync(), Is.False);
        }

        [Test]
        public async Task ChannelInactiveStopsReader()
        {
            using CancellationTokenSource cancellationSource = new(10_000);
            await using IAsyncEnumerator<PooledUdpReceiveResult> enumerator = _handler
                .ReadMessagesAsync(cancellationSource.Token)
                .GetAsyncEnumerator(cancellationSource.Token);
            ValueTask<bool> readTask = enumerator.MoveNextAsync();

            _handler.ChannelInactive(Substitute.For<IChannelHandlerContext>());

            Assert.That(await readTask.AsTask().WaitAsync(cancellationSource.Token), Is.False);
        }
    }
}
