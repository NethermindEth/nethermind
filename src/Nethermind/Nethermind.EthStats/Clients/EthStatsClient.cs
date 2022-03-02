//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.EthStats.Messages;
using Nethermind.Logging;
using Websocket.Client;

[assembly: InternalsVisibleTo("Nethermind.EthStats.Test")]

namespace Nethermind.EthStats.Clients
{
    public class EthStatsClient : IEthStatsClient, IDisposable
    {
        private const string ServerPingMessage = "primus::ping::";
        private readonly string _urlFromConfig;
        private readonly int _reconnectionInterval;
        private readonly IMessageSender _messageSender;
        private readonly ILogger _logger;
        private IWebsocketClient? _client;

        public EthStatsClient(
            string? urlFromConfig,
            int reconnectionInterval,
            IMessageSender? messageSender,
            ILogManager? logManager)
        {
            _urlFromConfig = urlFromConfig ?? throw new ArgumentNullException(nameof(urlFromConfig));
            _reconnectionInterval = reconnectionInterval;
            _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentException(nameof(logManager));
        }

        internal string BuildUrl()
        {
            string websocketUrl = _urlFromConfig;
            Uri? websocketUri;
            if (!Uri.TryCreate(_urlFromConfig, UriKind.Absolute, out websocketUri))
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
            Uri url = new Uri(websocketUrl);
            _client = new WebsocketClient(url)
            {
                ErrorReconnectTimeout = TimeSpan.FromMilliseconds(_reconnectionInterval),
                ReconnectTimeout = null
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
            string? serverTimeString = message.Split("::").LastOrDefault()?.Replace("\"", string.Empty);
            long serverTime = serverTimeString is null ? clientTime : long.Parse(serverTimeString);
            long latency = clientTime >= serverTime ? clientTime - serverTime : serverTime - clientTime;
            string pong = $"\"primus::pong::{serverTime}\"";
            if (_logger.IsDebug) _logger.Debug($"Sending 'pong' message to ETH stats...");

            if (_client is not null)
            {
                _client.Send(pong);
                await _messageSender.SendAsync(_client, new LatencyMessage(latency));
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
