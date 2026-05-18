// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.EthStats.Test
{
    public class EthStatsMessageParserTests
    {
        [Test]
        public void Can_parse_history_request()
        {
            bool parsed = EthStatsMessageParser.TryParse("""{"emit":["history",{"min":1,"max":3}]}""", out EthStatsIncomingMessage message);

            Assert.That(parsed, Is.True);
            Assert.That(message.MessageType, Is.EqualTo(EthStatsIncomingMessageType.History));
            Assert.That(message.HistoryRequest, Is.EqualTo(new EthStatsHistoryRequest(1, 3)));
        }

        [Test]
        public void Can_parse_node_ping()
        {
            bool parsed = EthStatsMessageParser.TryParse("""{"emit":["node-ping",{"clientTime":42}]}""", out EthStatsIncomingMessage message);

            Assert.That(parsed, Is.True);
            Assert.That(message.MessageType, Is.EqualTo(EthStatsIncomingMessageType.NodePing));
            Assert.That(message.NodeTiming, Is.EqualTo(new EthStatsNodeTiming(42, null)));
        }

        [Test]
        public void Can_parse_node_pong()
        {
            bool parsed = EthStatsMessageParser.TryParse("""{"emit":["node-pong",{"clientTime":42,"serverTime":84}]}""", out EthStatsIncomingMessage message);

            Assert.That(parsed, Is.True);
            Assert.That(message.MessageType, Is.EqualTo(EthStatsIncomingMessageType.NodePong));
            Assert.That(message.NodeTiming, Is.EqualTo(new EthStatsNodeTiming(42, 84)));
        }

        [Test]
        public void Ignores_invalid_payload()
        {
            bool parsed = EthStatsMessageParser.TryParse("""{"emit":["history",{"min":"bad","max":3}]}""", out _);

            Assert.That(parsed, Is.False);
        }
    }
}
