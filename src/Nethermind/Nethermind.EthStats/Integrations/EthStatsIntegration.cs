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
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.EthStats.Messages;
using Nethermind.EthStats.Messages.Models;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.TxPool;
using Websocket.Client;
using Block = Nethermind.Core.Block;
using Transaction = Nethermind.EthStats.Messages.Models.Transaction;

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
        private readonly ITxPool _txPool;
        private readonly IBlockTree _blockTree;
        private readonly IPeerManager _peerManager;
        private readonly ILogger _logger;
        private IWebsocketClient? _websocketClient;
        private bool _connected;
        private long _lastBlockProcessedTimestamp;
        private Timer? _timer;
        private const int ThrottlingThreshold = 25;
        private const int SendStatsInterval = 1000;

        public EthStatsIntegration(
            string name,
            string node,
            int port,
            string network,
            string protocol,
            string api,
            string client,
            string contact,
            bool canUpdateHistory,
            string secret,
            IEthStatsClient? ethStatsClient,
            IMessageSender? sender,
            ITxPool? txPool,
            IBlockTree? blockTree,
            IPeerManager? peerManager,
            ILogManager? logManager)
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
            _ethStatsClient = ethStatsClient ?? throw new ArgumentNullException(nameof(ethStatsClient));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task InitAsync()
        {
            _timer = new Timer {Interval = SendStatsInterval};
            _timer.Elapsed += TimerOnElapsed;
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
            _websocketClient = await _ethStatsClient.InitAsync();
            if (_logger.IsInfo) _logger.Info("Initial connection, sending 'hello' message...");
            await SendHelloAsync();
            _connected = true;

            Run(_timer);
        }

        private void Run(Timer timer)
        {
            if (_websocketClient is null)
            {
                if(_logger.IsError) _logger.Error("WebSocket client initialization failed");
                return;
            }
            
            _websocketClient.ReconnectionHappened.Subscribe(async _ =>
            {
                if (_logger.IsInfo) _logger.Info("ETH Stats reconnected, sending 'hello' message...");
                await SendHelloAsync();
                _connected = true;
            });

            _websocketClient.DisconnectionHappened.Subscribe(reason =>
            {
                _connected = false;
                if (_logger.IsInfo) _logger.Info($"ETH Stats disconnected, reason: {reason}");
            });

            timer.Start();
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

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            Block? block = e.Block;

            if (!_connected)
            {
                return;
            }

            long timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
            if (timestamp - _lastBlockProcessedTimestamp < ThrottlingThreshold)
            {
                return;
            }

            if (block == null)
            {
                _logger.Error($"{nameof(EthStatsIntegration)} received null as the new head block.");
                return;
            }

            if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'block', 'pending' messages...");
            _lastBlockProcessedTimestamp = timestamp;
            SendBlockAsync(block);
            SendPendingAsync(_txPool.GetPendingTransactionsCount());
        }

        public void Dispose()
        {
            _connected = false;
            _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;
            Timer? timer = _timer;
            if (timer is not null)
            {
                timer.Elapsed -= TimerOnElapsed;
                timer.Stop();
                timer.Dispose();
            }
            
            _websocketClient?.Dispose();
        }

        private Task SendHelloAsync()
            => _sender.SendAsync(_websocketClient!, new HelloMessage(_secret, new Info(_name, _node, _port, _network,
                _protocol,
                _api, Platform.GetPlatformName(), RuntimeInformation.OSArchitecture.ToString(), _client,
                _contact, _canUpdateHistory)));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private Task SendBlockAsync(Block block)
            => _sender.SendAsync(_websocketClient!, new BlockMessage(
                new Messages.Models.Block(
                    block.Number,
                    (block.Hash ?? Keccak.Zero).ToString() ,
                    (block.ParentHash ?? Keccak.Zero).ToString(),
                    (long)block.Timestamp,
                    (block.Author ?? block.Beneficiary ?? Address.Zero).ToString(),
                    block.GasUsed,
                    block.GasLimit,
                    block.Difficulty.ToString(),
                    (block.TotalDifficulty ?? 0).ToString(),
                    block.Transactions.Select(t => new Transaction((t.Hash ?? Keccak.Zero).ToString())),
                    (block.TxRoot ?? Keccak.Zero).ToString(),
                    (block.StateRoot ?? Keccak.Zero).ToString(),
                    block.Ommers.Select(_ => new Uncle()))));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private Task SendPendingAsync(int pending)
            => _sender.SendAsync(_websocketClient!, new PendingMessage(new PendingStats(pending)));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private Task SendStatsAsync()
            => _sender.SendAsync(_websocketClient!, new StatsMessage(new Messages.Models.Stats(true, true, false, 0,
                _peerManager.ActivePeers.Count, (long)20.GWei(), 100)));
    }
}
