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
using System.IO;
using System.Net.WebSockets;
using FluentAssertions;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.WebSockets.Test
{
    [TestFixture]
    public class NdmWebSocketsModuleTests
    {
        private NdmWebSocketsModule _module;

        [SetUp]
        public void Setup()
        {
            _module = new NdmWebSocketsModule(Substitute.For<INdmApi>());
        }

        [Test]
        public void Name_is_ndm()
        {
            _module.Name.Should().Be("ndm");
        }

        [Test]
        public void Try_init_returns_true()
        {
            _module.TryInit(null).Should().BeTrue();
        }

        [Test]
        public void Can_create_client()
        {
            ISocketsClient client = _module.CreateClient(WebSocket.CreateFromStream(Stream.Null, false, "subprotocol", TimeSpan.FromMinutes(1)), "test");
            client.Should().NotBeNull();
        }

        [Test]
        public void Can_remove_client()
        {
            ISocketsClient client = _module.CreateClient(WebSocket.CreateFromStream(Stream.Null, false, "subprotocol", TimeSpan.FromMinutes(1)), "test");
            client.Should().NotBeNull();
            _module.RemoveClient(client.Id);
        }

        [Test]
        public void Can_send_message()
        {
            ISocketsClient client = _module.CreateClient(WebSocket.CreateFromStream(Stream.Null, false, "subprotocol", TimeSpan.FromMinutes(1)), "test");
            client.Should().NotBeNull();
            _module.SendAsync(new SocketsMessage("test", "client", "data"));
        }
    }
}
