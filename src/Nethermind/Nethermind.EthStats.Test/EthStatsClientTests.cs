// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Messages;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Websocket.Client;

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

        [Test]
        [CancelAfter(30_000)]
        public async Task Init_keeps_retrying_when_server_is_unavailable_at_startup([Values("ws://127.0.0.1:{0}/api", "ws://127.0.0.1:{0}/api/", "ws://127.0.0.1:{0}", "http://127.0.0.1:{0}")] string configUrl, CancellationToken cancellationToken)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            using EthStatsClient ethClient = new(string.Format(configUrl, port), 100, Substitute.For<IMessageSender>(), LimboLogs.Instance);
            IWebsocketClient client = await ethClient.InitAsync();
            Assert.That(client.Url.AbsoluteUri.TrimEnd('/'), Is.EqualTo($"ws://127.0.0.1:{port}/api"));

            listener = new(IPAddress.Loopback, port);
            listener.Start();
            try
            {
                using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
                Assert.That(connection.Connected, Is.True);
            }
            finally
            {
                listener.Stop();
            }
        }

        [TestCase("\"primus::ping::1690000000000\"", 1, TestName = "Ping_handler_logs_send_failure_instead_of_throwing")]
        [TestCase("\"primus::pong::1690000000000\"", 0, TestName = "Ping_handler_ignores_non_ping_message")]
        [CancelAfter(30_000)]
        public async Task Ping_handler_answers_only_pings_and_logs_send_failure(string message, int expectedSends)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            IMessageSender sender = Substitute.For<IMessageSender>();
            sender.SendAsync(Arg.Any<IWebsocketClient>(), Arg.Any<LatencyMessage>(), Arg.Any<string>()).Returns(Task.FromException(new InvalidOperationException()));
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsError.Returns(true);
            ILogger clientLogger = new(logger);
            ILogManager logManager = Substitute.For<ILogManager>();
            logManager.GetClassLogger<EthStatsClient>().Returns(clientLogger);

            using EthStatsClient ethClient = new($"ws://127.0.0.1:{port}/api", 100, sender, logManager);
            IWebsocketClient client = await ethClient.InitAsync();

            client.StreamFakeMessage(ResponseMessage.TextMessage(message));

            await sender.Received(expectedSends).SendAsync(Arg.Any<IWebsocketClient>(), Arg.Any<LatencyMessage>(), Arg.Any<string>());
            logger.Received(expectedSends).Error(Arg.Any<string>(), Arg.Any<InvalidOperationException>());
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
