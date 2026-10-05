// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.EthStats.Messages;
using Nethermind.EthStats.Messages.Models;
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

    private static IEnumerable<IMessage> Messages()
    {
        Block block = new(1, "0x01", "0x02", 3, "0x03", 4, 5, "6", "7", [new Transaction("0x04")], "0x05", "0x06", [new Uncle()]);
        yield return new BlockMessage(block);
        yield return new HelloMessage("secret", new Info("name", "node", 30303, "1", "eth/68", "no", "linux", "6.1", "Nethermind", "contact", true));
        yield return new HistoryMessage([block]);
        yield return new LatencyMessage(7);
        yield return new NodePingMessage(8);
        yield return new NodePongMessage(null, 9);
        yield return new PendingMessage(new PendingStats(10));
        yield return new PingMessage(11);
        yield return new StatsMessage(new Messages.Models.Stats(true, false, true, 12, 13, 14, 15));
    }

    [TestCaseSource(nameof(Messages))]
    public async Task Payload_matches_reflection_serialization(IMessage message)
    {
        // EthStats servers parse these payloads; source-generated metadata must keep the shape reflection produced.
        IWebsocketClient client = Substitute.For<IWebsocketClient>();
        string payload = null;
        client.Send(Arg.Do<string>(sent => payload = sent));

        await new MessageSender("test-node", LimboLogs.Instance).SendAsync(client, message, "type");

        Assert.That(payload, Is.EqualTo(JsonSerializer.Serialize(new { emit = new object[] { "type", message } }, JsonSerializerOptions.Web)));
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
