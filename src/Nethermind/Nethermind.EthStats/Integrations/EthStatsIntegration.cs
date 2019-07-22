/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Core.Extensions;
using Nethermind.EthStats.Messages;
using Nethermind.EthStats.Messages.Models;
using Nethermind.Logging;
using Nethermind.Network;
using Websocket.Client;

namespace Nethermind.EthStats.Integrations
{
    public class EthStatsIntegration : IEthStatsIntegration
    {
        private readonly string _name;
        private readonly string _node;
        private readonly int _port;
        private readonly string _network;
        private readonly string _protocol;
        private readonly string _api;
        private readonly string _client;
        private readonly string _contact;
        private readonly bool _canUpdateHistory;
        private readonly string _secret;
        private readonly IEthStatsClient _ethStatsClient;
        private readonly IMessageSender _sender;
        private readonly IBlockTree _blockTree;
        private readonly IPeerManager _peerManager;
        private readonly ILogger _logger;
        private IWebsocketClient _websocketClient;
        private bool _connected;
        private long _lastBlockProcessedTimestamp;
        private const int ThrottlingThreshold = 25;
        private const int SendStatsInterval = 1000;

        public EthStatsIntegration(string name, string node, int port, string network, string protocol, string api,
            string client, string contact, bool canUpdateHistory, string secret, IEthStatsClient ethStatsClient,
            IMessageSender sender, IBlockTree blockTree, IPeerManager peerManager, ILogManager logManager)
        {
            _name = name;
            _node = node;
            _port = port;
            _network = network;
            _protocol = protocol;
            _api = api;
            _client = client;
            _contact = contact;
            _canUpdateHistory = canUpdateHistory;
            _secret = secret;
            _ethStatsClient = ethStatsClient;
            _sender = sender;
            _blockTree = blockTree;
            _peerManager = peerManager;
            _logger = logManager.GetClassLogger();
        }

        public async Task InitAsync()
        {
            var timer = new System.Timers.Timer {Interval = SendStatsInterval};
            timer.Elapsed += TimerOnElapsed;
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
            var exitEvent = new ManualResetEvent(false);
            using (_websocketClient = await _ethStatsClient.InitAsync())
            {
                if (_logger.IsInfo) _logger.Info("Initial connection, sending 'hello' message...");
                await SendHelloAsync();
                _connected = true;
                _websocketClient.ReconnectionHappened.Subscribe(async reason =>
                {
                    if (_logger.IsInfo) _logger.Info("ETH Stats reconnected, sending 'hello' message...");
                    await SendHelloAsync();
                    _connected = true;
                });
                _websocketClient.DisconnectionHappened.Subscribe(reason =>
                {
                    _connected = false;
                    if (_logger.IsWarn) _logger.Warn($"ETH Stats disconnected, reason: {reason}");
                });

                timer.Start();
                exitEvent.WaitOne();
            }

            _connected = false;
            timer.Stop();
            timer.Dispose();
            _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;
            timer.Elapsed -= TimerOnElapsed;
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_connected)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'stats' message...");
            SendStatsAsync();
        }

        private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
        {
            if (!_connected)
            {
                return;
            }

            var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
            if (timestamp - _lastBlockProcessedTimestamp < ThrottlingThreshold)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'block', 'pending' messages...");
            _lastBlockProcessedTimestamp = timestamp;
            SendBlockAsync(e.Block);
            SendPendingAsync(e.Block.Transactions?.Length ?? 0);
        }

        private Task SendHelloAsync()
            => _sender.SendAsync(_websocketClient, new HelloMessage(_secret, new Info(_name, _node, _port, _network,
                _protocol,
                _api, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), _client,
                _contact, _canUpdateHistory)));

        private Task SendBlockAsync(Core.Block block)
            => _sender.SendAsync(_websocketClient, new BlockMessage(new Block(block.Number, block.Hash?.ToString(),
                block.ParentHash?.ToString(),
                (long) block.Timestamp, block.Author?.ToString(), block.GasUsed, block.GasLimit,
                block.Difficulty.ToString(), block.TotalDifficulty?.ToString(),
                block.Transactions?.Select(t => new Transaction(t.Hash?.ToString())) ?? Enumerable.Empty<Transaction>(),
                block.TransactionsRoot.ToString(), block.StateRoot.ToString(),
                block.Ommers?.Select(o => new Uncle()) ?? Enumerable.Empty<Uncle>())));

        private Task SendPendingAsync(int pending)
            => _sender.SendAsync(_websocketClient, new PendingMessage(new PendingStats(pending)));

        private Task SendStatsAsync()
            => _sender.SendAsync(_websocketClient, new StatsMessage(new Messages.Models.Stats(true, true, false, 0,
                _peerManager.ActivePeers.Count, (long) 20.GWei(), 100)));
    }
}