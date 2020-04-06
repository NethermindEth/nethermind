//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Text;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.WebSockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.WebSockets.Test
{
    [TestFixture]
    public class NdmWebSocketsClientTests
    {
        [Test]
        public void Client_name_is_copied_from_ws_client()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Client.Returns(nameof(NdmWebSocketsClientTests));

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.Client.Should().Be(nameof(NdmWebSocketsClientTests));
        }

        [Test]
        public void Id_is_copied_from_ws_client()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.Id.Should().Be(nameof(NdmWebSocketsClientTests) + "_id");
        }

        [Test]
        public void Forwards_messages_to_ws_client()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            WebSocketsMessage message = new WebSocketsMessage("type", "client", "data");
            client.SendAsync(message);

            webSocketsClient.Received().SendAsync(message);
        }

        [Test]
        public void Forwards_raw_messages_to_ws_client()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.SendRawAsync("raw");

            webSocketsClient.Received().SendRawAsync("raw");
        }

        [Test]
        public void Can_receive_invalid_data_asset_id()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.ReceiveAsync(Encoding.ASCII.GetBytes("a|b|c"));
        }

        [Test]
        public void Can_receive_invalid_data_parts()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.ReceiveAsync(Encoding.ASCII.GetBytes("a|b"));
            client.ReceiveAsync(Encoding.ASCII.GetBytes("a|b|c|d"));
            dataPublisher.DidNotReceiveWithAnyArgs().Publish(null);
        }

        [Test]
        public void Can_receive_data()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.ReceiveAsync(Encoding.ASCII.GetBytes($"{Keccak.Zero.Bytes.ToHexString(false)}|b|c"));
            dataPublisher.Received().Publish(Arg.Is<DataAssetData>(dad => dad.Data == "c" && dad.AssetId == Keccak.Zero));
        }

        [Test]
        public void Can_receive_data_without_asset_id()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.ReceiveAsync(Encoding.ASCII.GetBytes("|b|c"));
            dataPublisher.DidNotReceiveWithAnyArgs().Publish(null);
        }

        [Test]
        public void Can_receive_null_or_empty_data()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            webSocketsClient.Id.Returns(nameof(NdmWebSocketsClientTests) + "_id");

            INdmDataPublisher dataPublisher = Substitute.For<INdmDataPublisher>();
            NdmWebSocketsClient client = new NdmWebSocketsClient(webSocketsClient, dataPublisher);
            client.ReceiveAsync(null);
            client.ReceiveAsync(Bytes.Empty);
        }
    }
}