// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using DotNetty.Transport.Channels.Sockets;
using FluentAssertions;
using Nethermind.Logging;
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
        public async Task CleanUp()
        {
            await _channel.CloseAsync();
        }

        [TestCase("127.0.0.1", "127.0.0.1")]
        [TestCase("::ffff:127.0.0.1", "127.0.0.1")]
        public async Task ForwardsSentMessageToChannel(string destinationAddress, string expectedDestinationAddress)
        {
            byte[] data = [1, 2, 3];
            IPEndPoint to = new(IPAddress.Parse(destinationAddress), 10001);
            IPEndPoint expectedTo = new(IPAddress.Parse(expectedDestinationAddress), 10001);

            await _handler.SendAsync(data, to);

            DatagramPacket packet = _channel.ReadOutbound<DatagramPacket>();
            packet.Should().NotBeNull();
            packet.Content.ReadAllBytesAsArray().Should().BeEquivalentTo(data);
            packet.Recipient.Should().Be(expectedTo);
        }

        [TestCase("127.0.0.1", "127.0.0.1")]
        [TestCase("::ffff:127.0.0.1", "127.0.0.1")]
        public async Task ForwardsReceivedMessageToReader(string senderAddress, string expectedSenderAddress)
        {
            byte[] data = [1, 2, 3];
            IPEndPoint from = new(IPAddress.Parse(senderAddress), 10000);
            IPEndPoint expectedFrom = new(IPAddress.Parse(expectedSenderAddress), 10000);
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10001");

            using var cancellationSource = new CancellationTokenSource(10_000);
            IAsyncEnumerator<UdpReceiveResult> enumerator = _handler
                .ReadMessagesAsync(cancellationSource.Token)
                .GetAsyncEnumerator(cancellationSource.Token);

            var ctx = Substitute.For<IChannelHandlerContext>();

            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));

            (await enumerator.MoveNextAsync()).Should().BeTrue();
            UdpReceiveResult forwardedPacket = enumerator.Current;

            forwardedPacket.Should().NotBeNull();
            forwardedPacket.Buffer.Should().BeEquivalentTo(data);
            forwardedPacket.RemoteEndPoint.Should().Be(expectedFrom);
        }

        [TestCase(0)]
        [TestCase(1280 + 1)]
        public async Task SkipsMessagesOfInvalidSize(int size)
        {
            byte[] data = [1, 2, 3];
            byte[] invalidData = Enumerable.Repeat((byte)1, size).ToArray();
            var from = IPEndPoint.Parse("127.0.0.1:10000");
            var to = IPEndPoint.Parse("127.0.0.1:10001");

            using var cancellationSource = new CancellationTokenSource(10_000);
            IAsyncEnumerator<UdpReceiveResult> enumerator = _handler
                .ReadMessagesAsync(cancellationSource.Token)
                .GetAsyncEnumerator(cancellationSource.Token);

            var ctx = Substitute.For<IChannelHandlerContext>();

            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])invalidData.Clone()), from, to));
            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));
            _handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])invalidData.Clone()), from, to));
            _handler.Close();

            (await enumerator.MoveNextAsync()).Should().BeTrue();
            enumerator.Current.Buffer.Should().BeEquivalentTo(data);
            (await enumerator.MoveNextAsync()).Should().BeFalse();
        }
    }
}
