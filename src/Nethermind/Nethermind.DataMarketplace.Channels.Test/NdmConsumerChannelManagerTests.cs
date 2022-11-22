// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Channels.Grpc;
using Nethermind.DataMarketplace.WebSockets;
using Nethermind.Grpc;
using Nethermind.Logging;
using Nethermind.Sockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Channels.Test
{
    [TestFixture]
    public class NdmConsumerChannelManagerTests
    {
        [Test]
        public void Can_add_various_channel_types()
        {
            NdmConsumerChannelManager manager = new NdmConsumerChannelManager();
            manager.Add(new JsonRpcNdmConsumerChannel(LimboLogs.Instance));
            manager.Add(new GrpcNdmConsumerChannel(Substitute.For<IGrpcServer>()));
            manager.Add(new NdmWebSocketsConsumerChannel(Substitute.For<ISocketsClient>()));
        }

        [Test]
        public void Json_rpc_channel_has_max_capacity()
        {
            JsonRpcNdmConsumerChannel channel = new JsonRpcNdmConsumerChannel(LimboLogs.Instance);
            for (int i = 0; i < JsonRpcNdmConsumerChannel.MaxCapacity + 1; i++)
            {
                channel.PublishAsync(Keccak.Zero, "client", "data");
            }

            for (int i = 0; i < JsonRpcNdmConsumerChannel.MaxCapacity; i++)
            {
                channel.Pull(Keccak.Zero).Should().NotBeNull();
            }

            channel.Pull(Keccak.Zero).Should().BeNull();
        }

        [Test]
        public async Task Can_publish_on_various_channel_types()
        {
            NdmConsumerChannelManager manager = new NdmConsumerChannelManager();

            ISocketsClient client = Substitute.For<ISocketsClient>();
            INdmConsumerChannel[] channels = new INdmConsumerChannel[]
            {
                new JsonRpcNdmConsumerChannel(LimboLogs.Instance),
                new GrpcNdmConsumerChannel(Substitute.For<IGrpcServer>()),
                new NdmWebSocketsConsumerChannel(client),
            };

            ((JsonRpcNdmConsumerChannel)channels[0]).Pull(Keccak.Zero).Should().BeNull();

            for (int i = 0; i < 3; i++)
            {
                manager.Add(channels[i]);
            }

            channels[0].Type.Should().Be(NdmConsumerChannelType.JsonRpc);
            channels[1].Type.Should().Be(NdmConsumerChannelType.Grpc);

            await manager.PublishAsync(Keccak.Zero, "client1", "data1");
            await manager.PublishAsync(Keccak.Zero, "client2", "data2");

            for (int i = 0; i < 3; i++)
            {
                manager.Remove(channels[i]);
            }

            await manager.PublishAsync(Keccak.Zero, "client3", "data3");

            for (int i = 0; i < 3; i++)
            {
                await client.Received().SendAsync(Arg.Is<SocketsMessage>(wm => wm.Client == "client1"));
                await client.Received().SendAsync(Arg.Is<SocketsMessage>(wm => wm.Client == "client2"));
                await client.DidNotReceive().SendAsync(Arg.Is<SocketsMessage>(wm => wm.Client == "client3"));
            }

            ((JsonRpcNdmConsumerChannel)channels[0]).Pull(Keccak.Zero).Should().NotBeNull();
            ((JsonRpcNdmConsumerChannel)channels[0]).Pull(Keccak.Zero).Should().NotBeNull();
            ((JsonRpcNdmConsumerChannel)channels[0]).Pull(Keccak.Zero).Should().BeNull();
        }
    }
}
