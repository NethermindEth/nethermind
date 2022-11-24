// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Jint.Native.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.WebSockets.Test
{
    [TestFixture]
    public class NdmWebSocketsClientTests
    {

        private IJsonSerializer _serializer = Substitute.For<IJsonSerializer>();

        [Test]
        public void Forwards_messages_to_ws_handler()
        {
            ISocketHandler _handler = Substitute.For<ISocketHandler>();
            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();

            NdmWebSocketsClient client = new(nameof(NdmWebSocketsClientTests), _handler, dataPublisher, _serializer);
            SocketsMessage message = new("type", nameof(NdmWebSocketsClientTests), "data");
            client.SendAsync(message);

            _handler.Received().SendRawAsync(Arg.Any<ArraySegment<byte>>());
        }

        [Test]
        public async Task Can_receive_invalid_data_asset_id()
        {
            ISocketHandler handler = Substitute.For<ISocketHandler>();
            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();

            NdmWebSocketsClient client = new(nameof(NdmWebSocketsClientTests), handler, dataPublisher, _serializer);
            await client.ProcessAsync(Encoding.ASCII.GetBytes("a|b|c"));
        }

        [Test]
        public async Task Can_receive_invalid_data_parts()
        {
            ISocketHandler handler = Substitute.For<ISocketHandler>();
            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();

            NdmWebSocketsClient client = new(nameof(NdmWebSocketsClientTests), handler, dataPublisher, _serializer);
            await client.ProcessAsync(Encoding.ASCII.GetBytes("a|b"));
            await client.ProcessAsync(Encoding.ASCII.GetBytes("a|b|c|d"));
            dataPublisher.DidNotReceiveWithAnyArgs().Publish(null);
        }

        [Test]
        public async Task Can_receive_data()
        {
            ISocketHandler handler = Substitute.For<ISocketHandler>();
            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();

            NdmWebSocketsClient client = new(nameof(NdmWebSocketsClientTests), handler, dataPublisher, _serializer);
            await client.ProcessAsync(Encoding.ASCII.GetBytes($"{Keccak.Zero.Bytes.ToHexString(false)}|b|c"));
            dataPublisher.Received().Publish(Arg.Is<DataAssetData>(dad => dad.Data == "c" && dad.AssetId == Keccak.Zero));
        }

        [Test]
        public async Task Can_receive_data_without_asset_id()
        {
            ISocketHandler handler = Substitute.For<ISocketHandler>();
            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();

            NdmWebSocketsClient client = new(nameof(NdmWebSocketsClientTests), handler, dataPublisher, _serializer);
            await client.ProcessAsync(Encoding.ASCII.GetBytes("|b|c"));
            dataPublisher.DidNotReceiveWithAnyArgs().Publish(null);
        }

        [Test]
        public async Task Can_receive_null_or_empty_data()
        {
            ISocketHandler handler = Substitute.For<ISocketHandler>();
            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();

            NdmWebSocketsClient client = new(nameof(NdmWebSocketsClientTests), handler, dataPublisher, _serializer);
            await client.ProcessAsync(null);
            await client.ProcessAsync(Array.Empty<byte>());
        }
    }
}
