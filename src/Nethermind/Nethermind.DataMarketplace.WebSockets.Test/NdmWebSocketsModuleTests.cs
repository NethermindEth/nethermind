// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            ISocketsClient client = _module.CreateClient(WebSocket.CreateFromStream(Stream.Null, false, "subprotocol", TimeSpan.FromMinutes(1)), "test", 1);
            client.Should().NotBeNull();
        }

        [Test]
        public void Can_remove_client()
        {
            ISocketsClient client = _module.CreateClient(WebSocket.CreateFromStream(Stream.Null, false, "subprotocol", TimeSpan.FromMinutes(1)), "test", 1);
            client.Should().NotBeNull();
            _module.RemoveClient(client.Id);
        }

        [Test]
        public void Can_send_message()
        {
            ISocketsClient client = _module.CreateClient(WebSocket.CreateFromStream(Stream.Null, false, "subprotocol", TimeSpan.FromMinutes(1)), "test", 1);
            client.Should().NotBeNull();
            _module.SendAsync(new SocketsMessage("test", "client", "data"));
        }
    }
}
