// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.EthStats.Messages;
using Nethermind.Logging;
using Websocket.Client;

[assembly: InternalsVisibleTo("Nethermind.EthStats.Test")]

namespace Nethermind.EthStats.Clients
{
    public class EthStatsClient(
        string? urlFromConfig,
        int reconnectionInterval,
        IMessageSender? messageSender,
        ILogManager? logManager) : IEthStatsClient, IDisposable
    {
        private const string ServerPingMessage = "primus::ping::";
        private const int ReconnectTimeoutMultiplier = 6;
        private readonly string _urlFromConfig = urlFromConfig ?? throw new ArgumentNullException(nameof(urlFromConfig));
        private readonly int _reconnectionInterval = reconnectionInterval;
        private readonly IMessageSender _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        private readonly ILogger _logger = logManager?.GetClassLogger<EthStatsClient>() ?? throw new ArgumentNullException(nameof(logManager));
        private IWebsocketClient? _client;

        internal string BuildUrl()
        {
            string websocketUrl = _urlFromConfig;
            if (!Uri.TryCreate(_urlFromConfig, UriKind.Absolute, out Uri? websocketUri))
            {
                ThrowIncorrectUrl();
            }
            if (websocketUri!.Scheme != Uri.UriSchemeWs && websocketUri!.Scheme != Uri.UriSchemeWss)
            {
                UriBuilder uriBuilder = null!;
                if (websocketUri.Scheme == Uri.UriSchemeHttp)
                {
                    uriBuilder = new UriBuilder(websocketUri)
                    {
                        Scheme = Uri.UriSchemeWs,
                        Port = websocketUri.IsDefaultPort ? -1 : websocketUri.Port
                    };
                }
                else if (websocketUri.Scheme == Uri.UriSchemeHttps)
                {
                    uriBuilder = new UriBuilder(websocketUri)
                    {
                        Scheme = Uri.UriSchemeWss,
                        Port = websocketUri.IsDefaultPort ? -1 : websocketUri.Port
                    };
                }
                else
                {
                    ThrowIncorrectUrl();
                }
                websocketUrl = uriBuilder.ToString();
                if (_logger.IsInfo) _logger.Info($"Moved ETH stats to: {websocketUrl}");

            }
            return websocketUrl;
        }

        public async Task<IWebsocketClient> InitAsync()
        {
            if (_logger.IsInfo) _logger.Info($"Starting ETH stats [{_urlFromConfig}]...");
            string websocketUrl = BuildUrl();
            Uri url = new(websocketUrl);
            _client = new WebsocketClient(url)
            {
                ErrorReconnectTimeout = TimeSpan.FromMilliseconds(_reconnectionInterval),
                ReconnectTimeout = TimeSpan.FromMilliseconds(_reconnectionInterval * ReconnectTimeoutMultiplier)
            };

            _client.MessageReceived.Subscribe(async message =>
            {
                if (_logger.IsDebug) _logger.Debug($"Received ETH stats message '{message}'");
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    return;
                }

                if (message.Text.Contains(ServerPingMessage))
                {
                    await HandlePingAsync(message.Text);
                }
            });

            try
            {
                await _client.StartOrFail();
            }
            catch (Exception)
            {
                if (!_client.Url.AbsoluteUri.EndsWith("/api"))
                {
                    if (_logger.IsInfo) _logger.Info($"Failed to connect to ethstats at {websocketUrl}. Adding '/api' at the end and trying again.");
                    _client.Url = new Uri(websocketUrl + "/api");
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"Failed to connect to ethstats at {websocketUrl}. Trying once again.");
                }

                await _client.StartOrFail();
            }

            if (_logger.IsDebug) _logger.Debug($"Started ETH stats.");

            return _client;
        }

        private void ThrowIncorrectUrl()
        {
            if (_logger.IsError) _logger.Error($"Incorrect ETH stats url: {_urlFromConfig}");
            throw new ArgumentException($"Incorrect ETH stats url: {_urlFromConfig}");
        }

        private async Task HandlePingAsync(string message)
        {
            long clientTime = Timestamper.Default.UnixTime.MillisecondsLong;
            bool parsed = TryParseServerTime(message, out long serverTime);
            // The server discards the pong's payload, so any timestamp works; latency is skipped to avoid a fabricated 0 ms reading.
            if (!parsed)
            {
                if (_logger.IsDebug) _logger.Debug($"Ignoring unparseable ETH stats ping timestamp in message '{message}'.");
                serverTime = clientTime;
            }
            long latency = clientTime >= serverTime ? clientTime - serverTime : serverTime - clientTime;
            string pong = $"\"primus::pong::{serverTime}\"";
            if (_logger.IsDebug) _logger.Debug($"Sending 'pong' message to ETH stats...");

            if (_client is not null)
            {
                _client.Send(pong);
                if (parsed) await _messageSender.SendAsync(_client, new LatencyMessage(latency));
            }
        }

        internal static bool TryParseServerTime(string message, out long serverTime)
        {
            ReadOnlySpan<char> span = message;
            int separatorIndex = span.LastIndexOf("::");
            // Wire frames quote only the outer message, so a well-formed timestamp segment
            // never contains a quote; an interior quote here is malformed input and is left
            // in place to fail parsing rather than stripped into a different number.
            ReadOnlySpan<char> serverTimeSpan = separatorIndex < 0 ? span : span[(separatorIndex + 2)..];
            return long.TryParse(serverTimeSpan.Trim('"'), out serverTime);
        }

        public void Dispose() => _client?.Dispose();
    }
}
