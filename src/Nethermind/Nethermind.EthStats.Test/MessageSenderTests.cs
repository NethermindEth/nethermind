// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.EthStats.Messages;
using Nethermind.EthStats.Senders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Websocket.Client;

namespace Nethermind.EthStats.Test;

public class MessageSenderTests
{
    [TestCase("node-ping", false, TestName = "Can_send_node_ping_with_ethstats_event_name")]
    [TestCase("node-pong", true, TestName = "Can_send_node_pong_with_ethstats_event_name")]
    public async Task Can_send_node_message_with_ethstats_event_name(string eventType, bool includeServerTime)
    {
        IWebsocketClient client = Substitute.For<IWebsocketClient>();
        MessageSender sender = new("test-node", LimboLogs.Instance);
        IMessage message = includeServerTime
            ? new NodePongMessage(42, 84)
            : new NodePingMessage(42);

        await sender.SendAsync(client, message, eventType);

        client.Received(1).Send(Arg.Is<string>(payload => ContainsExpectedPayload(payload, eventType, includeServerTime)));
    }

    private static bool ContainsExpectedPayload(string payload, string eventType, bool includeServerTime)
    {
        if (!payload.Contains($"\"{eventType}\"") ||
            !payload.Contains("\"id\":\"test-node\"") ||
            !payload.Contains("\"clientTime\":42"))
        {
            return false;
        }

        return !includeServerTime || payload.Contains("\"serverTime\":84");
    }
}
