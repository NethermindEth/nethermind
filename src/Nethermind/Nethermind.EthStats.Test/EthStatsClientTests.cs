// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.EthStats.Clients;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EthStats.Test
{
    public class EthStatsClientTests
    {
        [TestCase("https://localhost/api", "wss://localhost/api")]
        [TestCase("wss://localhost/api", "wss://localhost/api")]
        [TestCase("ws://localhost/api", "ws://localhost/api")]
        [TestCase("http://localhost/api", "ws://localhost/api")]
        [TestCase("https://localhost:8000/api", "wss://localhost:8000/api")]
        [TestCase("http://test://", "ws://test//")]
        public void Build_url_should_return_expected_results(string configUrl, string expectedUrl)
        {
            EthStatsClient ethClient = new(configUrl, 5000, Substitute.For<IMessageSender>(), LimboLogs.Instance);
            Assert.That(ethClient.BuildUrl(), Is.EqualTo(expectedUrl));
        }

        [Test]
        public void Incorrect_url_should_throw_exception([Values("http://test:://", "ftp://localhost", "http:/", "localhost")] string url)
        {
            EthStatsClient ethClient = new(url, 5000, Substitute.For<IMessageSender>(), LimboLogs.Instance);
            Assert.Throws<ArgumentException>(() => ethClient.BuildUrl());
        }

        [TestCase("primus::ping::", TestName = "Ping_with_empty_timestamp_tail_fails_to_parse")]
        [TestCase("primus::ping::not-a-number", TestName = "Ping_with_non_numeric_timestamp_tail_fails_to_parse")]
        [TestCase("primus::ping::   ", TestName = "Ping_with_whitespace_only_timestamp_tail_fails_to_parse")]
        [TestCase("primus::ping::99999999999999999999999999999", TestName = "Ping_with_timestamp_tail_overflowing_long_fails_to_parse")]
        [TestCase("no-separator-at-all", TestName = "Ping_with_no_separator_at_all_fails_to_parse")]
        [TestCase("\"primus::ping::\"", TestName = "Ping_in_wire_format_with_empty_timestamp_tail_fails_to_parse")]
        // A well-formed frame quotes only the outer message, so a quote inside the digits
        // means the frame is malformed; this is treated as unparseable rather than stripped.
        [TestCase("primus::ping::12\"34", TestName = "Ping_with_interior_quote_in_timestamp_is_rejected_as_malformed")]
        public void Try_parse_server_time_returns_false_for_unparseable_tail(string message)
        {
            bool parsed = EthStatsClient.TryParseServerTime(message, out long serverTime);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(parsed, Is.False);
                Assert.That(serverTime, Is.EqualTo(0L));
            }
        }

        [TestCase("primus::ping::1690000000000", 1690000000000L, TestName = "Ping_with_valid_positive_timestamp_is_parsed")]
        [TestCase("primus::ping::-42", -42L, TestName = "Ping_with_negative_timestamp_is_parsed")]
        [TestCase("primus::ping::9223372036854775807", long.MaxValue, TestName = "Ping_with_long_max_value_timestamp_is_parsed")]
        [TestCase("extra::primus::ping::1690000000000", 1690000000000L, TestName = "Ping_with_extra_separators_still_parses_last_segment")]
        [TestCase("\"primus::ping::1690000000000\"", 1690000000000L, TestName = "Ping_in_wire_format_with_surrounding_quotes_is_parsed")]
        [TestCase("12345", 12345L, TestName = "Ping_with_no_separator_and_numeric_payload_parses_whole_message")]
        public void Try_parse_server_time_returns_parsed_value_for_valid_tail(string message, long expectedServerTime)
        {
            bool parsed = EthStatsClient.TryParseServerTime(message, out long serverTime);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(parsed, Is.True);
                Assert.That(serverTime, Is.EqualTo(expectedServerTime));
            }
        }
    }
}
