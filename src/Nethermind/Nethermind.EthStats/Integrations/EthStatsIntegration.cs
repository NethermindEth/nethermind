// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EthStats.Messages;
using Nethermind.EthStats.Messages.Models;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.TxPool;
using Websocket.Client;
using Block = Nethermind.Core.Block;
using Timer = System.Timers.Timer;
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
        private readonly IGasPriceOracle _gasPriceOracle;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly bool _isMining;
        private IWebsocketClient? _websocketClient;
        private bool _connected;
        private long _lastBlockProcessedTimestamp;
        private Timer? _timer;
        private const int ThrottlingThreshold = 250;
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
            IGasPriceOracle? gasPriceOracle,
            IEthSyncingInfo ethSyncingInfo,
            bool isMining,
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
            _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
            _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
            _isMining = isMining;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task InitAsync()
        {
            _timer = new Timer { Interval = SendStatsInterval };
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
                if (_logger.IsError) _logger.Error("WebSocket client initialization failed");
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

        private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_connected)
            {
                if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'stats' message...");
                SendStatsAsync();
                SendPendingAsync(_txPool.GetPendingTransactionsCount());
            }
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            Block? block = e.Block;

            if (!_connected)
            {
                return;
            }

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (timestamp - _lastBlockProcessedTimestamp < ThrottlingThreshold)
            {
                return;
            }

            if (block is null)
            {
                _logger.Error($"{nameof(EthStatsIntegration)} received null as the new head block.");
                return;
            }

            if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'block', 'pending' messages...");
            _lastBlockProcessedTimestamp = timestamp;
            SendBlockAsync(block);
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
                    (block.Hash ?? Keccak.Zero).ToString(),
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
                    block.Uncles.Select(_ => new Uncle()))));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private Task SendPendingAsync(int pending)
            => _sender.SendAsync(_websocketClient!, new PendingMessage(new PendingStats(pending)));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private Task SendStatsAsync()
        {
            UInt256 gasPrice = _gasPriceOracle.GetGasPriceEstimate();
            if (gasPrice > long.MaxValue)
            {
                // EthStats doesn't work with UInt256, long should be enough
                if (_logger.IsTrace) _logger.Trace($"Gas price beyond the eth stats expected scope {gasPrice}");
                gasPrice = long.MaxValue;
            }

            return _sender.SendAsync(_websocketClient!, new StatsMessage(new Messages.Models.Stats(true, _ethSyncingInfo.IsSyncing(), _isMining, 0,
                _peerManager.ActivePeers.Count, (long)gasPrice, 100)));
        }
    }
}
