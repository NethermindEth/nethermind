// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Logging;
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
