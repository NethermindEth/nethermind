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

            _handler.Received().SendRawAsync(Arg.Any<string>());
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
