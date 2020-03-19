//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.EthStats.Messages;
using Nethermind.Logging;
using Websocket.Client;

namespace Nethermind.EthStats.Clients
{
    public class EthStatsClient : IEthStatsClient, IDisposable
    {
        private const string ServerPingMessage = "primus::ping::";
        private string _webSocketsUrl;
        private readonly int _reconnectionInterval;
        private readonly IMessageSender _messageSender;
        private readonly ILogger _logger;
        private IWebsocketClient _client;

        public EthStatsClient(string webSocketsUrl, int reconnectionInterval, IMessageSender messageSender,
            ILogManager logManager)
        {
            _webSocketsUrl = webSocketsUrl;
            _reconnectionInterval = reconnectionInterval;
            _messageSender = messageSender;
            _logger = logManager.GetClassLogger();
        }

        public async Task<IWebsocketClient> InitAsync()
        {
            if (_logger.IsInfo) _logger.Info($"Starting ETH stats [{_webSocketsUrl}]...");
            if (!_webSocketsUrl.StartsWith("wss"))
            {
                try
                {
                    using HttpClient httpClient = new HttpClient();
                    string host = _webSocketsUrl.Split("://").Last();
                    HttpResponseMessage response = await httpClient.GetAsync($"http://{host}");
                    Uri requestedUrl = response.RequestMessage.RequestUri;
                    if (requestedUrl.Scheme.Equals("https"))
                    {
                        _webSocketsUrl = $"wss://{host}";
                        if (_logger.IsInfo) _logger.Info($"Moved ETH stats to: {_webSocketsUrl}");
                    }
                }
                catch
                {
                    // ignored
                }
            }

            Uri url = new Uri(_webSocketsUrl);
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
            await _client.Start();
            if (_logger.IsDebug) _logger.Debug($"Started ETH stats.");

            return _client;
        }

        private async Task HandlePingAsync(string message)
        {
            long serverTime = long.Parse(message.Split("::").LastOrDefault()?.Replace("\"", string.Empty));
            long clientTime = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
            long latency = clientTime >= serverTime ? clientTime - serverTime : serverTime - clientTime;
            string pong = $"\"primus::pong::{serverTime}\"";
            if (_logger.IsDebug) _logger.Debug($"Sending 'pong' message to ETH stats...");
            _client.Send(pong);
            await _messageSender.SendAsync(_client, new LatencyMessage(latency));
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}