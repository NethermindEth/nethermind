// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Nethermind.EthStats.Test;

public class EthStatsMessageParserTests
{
    private static IEnumerable<TestCaseData> ParseCases()
    {
        yield return new TestCaseData(
            """{"emit":["history",{"min":1,"max":3}]}""",
            (int)EthStatsIncomingMessageType.History,
            1UL,
            3UL,
            null)
            .SetName("Can_parse_history_request");

        yield return new TestCaseData(
            """{"emit":["node-ping",{"clientTime":42}]}""",
            (int)EthStatsIncomingMessageType.NodePing,
            null,
            null,
            42L)
            .SetName("Can_parse_node_ping");

        yield return new TestCaseData(
            """{"emit":["node-pong",{"clientTime":42,"serverTime":84}]}""",
            (int)EthStatsIncomingMessageType.NodePong,
            null,
            null,
            42L)
            .SetName("Can_parse_node_pong");
    }

    [TestCaseSource(nameof(ParseCases))]
    public void Can_parse_message(
        string json,
        int expectedMessageType,
        ulong? expectedHistoryMin,
        ulong? expectedHistoryMax,
        long? expectedClientTime)
    {
        bool parsed = EthStatsMessageParser.TryParse(json, out EthStatsIncomingMessage message);
        EthStatsHistoryRequest? expectedHistoryRequest = expectedHistoryMin is null || expectedHistoryMax is null
            ? null
            : new EthStatsHistoryRequest(expectedHistoryMin.Value, expectedHistoryMax.Value);
        EthStatsNodeTiming? expectedNodeTiming = expectedClientTime is null
            ? null
            : new EthStatsNodeTiming(expectedClientTime);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(message.MessageType, Is.EqualTo((EthStatsIncomingMessageType)expectedMessageType));
            Assert.That(message.HistoryRequest, Is.EqualTo(expectedHistoryRequest));
            Assert.That(message.NodeTiming, Is.EqualTo(expectedNodeTiming));
        }
    }

    [Test]
    public void Ignores_invalid_payload()
    {
        bool parsed = EthStatsMessageParser.TryParse("""{"emit":["history",{"min":"bad","max":3}]}""", out _);

        Assert.That(parsed, Is.False);
    }
}
