// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.EthStats.Messages;
using Nethermind.EthStats.Senders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Websocket.Client;

namespace Nethermind.EthStats.Test
{
    public class MessageSenderTests
    {
        [Test]
        public async Task Can_send_node_ping_with_ethstats_event_name()
        {
            IWebsocketClient client = Substitute.For<IWebsocketClient>();
            MessageSender sender = new("test-node", LimboLogs.Instance);

            await sender.SendAsync(client, new NodePingMessage(42), "node-ping");

            client.Received(1).Send(Arg.Is<string>(payload =>
                payload.Contains("\"node-ping\"") &&
                payload.Contains("\"id\":\"test-node\"") &&
                payload.Contains("\"clientTime\":42")));
        }

        [Test]
        public async Task Can_send_node_pong_with_ethstats_event_name()
        {
            IWebsocketClient client = Substitute.For<IWebsocketClient>();
            MessageSender sender = new("test-node", LimboLogs.Instance);

            await sender.SendAsync(client, new NodePongMessage(42, 84), "node-pong");

            client.Received(1).Send(Arg.Is<string>(payload =>
                payload.Contains("\"node-pong\"") &&
                payload.Contains("\"id\":\"test-node\"") &&
                payload.Contains("\"clientTime\":42") &&
                payload.Contains("\"serverTime\":84")));
        }
    }
}
