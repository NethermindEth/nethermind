// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
using CoreBlock = Nethermind.Core.Block;
using CoreTransaction = Nethermind.Core.Transaction;
using Timer = System.Timers.Timer;
using EthStatsBlock = Nethermind.EthStats.Messages.Models.Block;
using EthStatsTransaction = Nethermind.EthStats.Messages.Models.Transaction;

namespace Nethermind.EthStats.Integrations
{
    public class EthStatsIntegration(
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
        IEthStatsClient ethStatsClient,
        IMessageSender sender,
        ITxPool txPool,
        IBlockTree blockTree,
        IPeerManager peerManager,
        IGasPriceOracle gasPriceOracle,
        IEthSyncingInfo ethSyncingInfo,
        bool isMining,
        TimeSpan sendStatsInterval,
        ILogManager logManager) : IEthStatsIntegration
    {
        private readonly string _name = name;
        private readonly string _node = node;
        private readonly int _port = port;
        private readonly string _network = network;
        private readonly string _protocol = protocol;
        private readonly string _api = api;
        private readonly string _client = client;
        private readonly string _contact = contact;
        private readonly bool _canUpdateHistory = canUpdateHistory;
        private readonly string _secret = secret;
        private readonly IEthStatsClient _ethStatsClient = ethStatsClient;
        private readonly IMessageSender _sender = sender;
        private readonly ITxPool _txPool = txPool;
        private readonly IBlockTree _blockTree = blockTree;
        private readonly IPeerManager _peerManager = peerManager;
        private readonly ILogger _logger = logManager.GetClassLogger<EthStatsIntegration>();
        private readonly IGasPriceOracle _gasPriceOracle = gasPriceOracle;
        private readonly IEthSyncingInfo _ethSyncingInfo = ethSyncingInfo;
        private readonly bool _isMining = isMining;
        private readonly TimeSpan _sendStatsInterval = sendStatsInterval > TimeSpan.Zero
                ? sendStatsInterval
                : throw new ArgumentOutOfRangeException(nameof(sendStatsInterval));

        private IWebsocketClient? _websocketClient;
        private bool _connected;
        private long _lastBlockProcessedTimestamp;
        private Timer? _timer;
        private const int ThrottlingThreshold = 250;
        private const int MaxHistoryBlocks = 64;
        private IDisposable? _messageSubscription;

        public async Task InitAsync()
        {
            _timer = new Timer { Interval = _sendStatsInterval.TotalMilliseconds };
            _timer.Elapsed += TimerOnElapsed;
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
            _websocketClient = await _ethStatsClient.InitAsync();

            // Subscribe to incoming messages before the explicit initial hello; reconnect events only cover later reconnects.
            Run(_timer);

            if (_logger.IsInfo) _logger.Info("Initial connection, sending 'hello' message...");
            await SendHelloAsync();
            _connected = true;
        }

        private void Run(Timer timer)
        {
            if (_websocketClient is null)
            {
                if (_logger.IsError) _logger.Error("WebSocket client initialization failed");
                return;
            }

            _websocketClient.ReconnectionHappened.Subscribe(_ => OnReconnectionHappened());

            _websocketClient.DisconnectionHappened.Subscribe(reason =>
            {
                _connected = false;
                if (_logger.IsInfo) _logger.Info($"ETH Stats disconnected, reason: {reason}");
            });

            _messageSubscription = _websocketClient.MessageReceived.Subscribe(message => _ = HandleIncomingMessageAsync(message.Text));
            timer.Start();
        }

        private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_connected)
                return;
            if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'stats' message...");
            _ = SendStatsAsync();
            _ = SendNodePingAsync();
            _ = SendPendingAsync(_txPool.GetPendingTransactionsCount() + _txPool.GetPendingBlobTransactionsCount());
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            CoreBlock? block = e.Block;

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
            _ = SendBlockAsync(block);
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

            _messageSubscription?.Dispose();
            _websocketClient?.Dispose();
        }

        private Task SendHelloAsync()
            => _sender.SendAsync(_websocketClient!, new HelloMessage(_secret, new Info(_name, _node, _port, _network,
                _protocol,
                _api, Platform.GetPlatformName(), RuntimeInformation.OSArchitecture.ToString(), _client,
                _contact, _canUpdateHistory)));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private Task SendBlockAsync(CoreBlock block)
            => _sender.SendAsync(_websocketClient!, new BlockMessage(CreateBlockModel(block)));

        private void OnReconnectionHappened()
            => _ = ReconnectHelloAsync();

        private async Task ReconnectHelloAsync()
        {
            try
            {
                if (_logger.IsInfo) _logger.Info("ETH Stats reconnected, sending 'hello' message...");
                await SendHelloAsync();
                _connected = true;
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"ETH Stats hello failed after reconnect: {e}");
            }
        }

        private Task SendHistoryAsync(EthStatsHistoryRequest request)
        {
            if (!TryNormalizeHistoryRange(request, out ulong min, out ulong max))
            {
                if (_logger.IsDebug) _logger.Debug($"Ignoring invalid ETH Stats history range {request.Min}-{request.Max}.");
                return Task.CompletedTask;
            }

            List<EthStatsBlock> history = new((int)(max - min + 1));
            for (ulong blockNumber = min; blockNumber <= max; blockNumber++)
            {
                CoreBlock? block = _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
                if (block is not null)
                {
                    history.Add(CreateBlockModel(block));
                }

                // Prevent ulong.MaxValue + 1 overflow on the for-loop increment.
                if (blockNumber == max)
                {
                    break;
                }
            }

            if (_logger.IsDebug) _logger.Debug($"ETH Stats sending 'history' message for range {min}-{max}.");
            return _sender.SendAsync(_websocketClient!, new HistoryMessage(history));
        }

        private Task SendNodePingAsync()
        {
            if (_websocketClient is null)
            {
                return Task.CompletedTask;
            }

            long clientTime = Timestamper.Default.UnixTime.MillisecondsLong;
            if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'node-ping' message...");
            return _sender.SendAsync(_websocketClient, new NodePingMessage(clientTime), "node-ping");
        }

        internal async Task HandleIncomingMessageAsync(string? message)
        {
            if (!EthStatsMessageParser.TryParse(message, out EthStatsIncomingMessage incomingMessage))
            {
                return;
            }

            try
            {
                Task handleTask = incomingMessage.MessageType switch
                {
                    EthStatsIncomingMessageType.History when incomingMessage.HistoryRequest is not null =>
                        SendHistoryAsync(incomingMessage.HistoryRequest.Value),
                    EthStatsIncomingMessageType.NodePing =>
                        SendNodePongAsync(incomingMessage.NodeTiming?.ClientTime),
                    EthStatsIncomingMessageType.NodePong =>
                        SendLatencyFromNodePongAsync(incomingMessage.NodeTiming),
                    _ => HandleUnknownMessageAsync(incomingMessage.EventTypeName)
                };

                await handleTask;
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to handle ETH Stats message '{incomingMessage.EventTypeName}': {e}");
            }
        }

        private Task SendNodePongAsync(long? clientTime)
        {
            if (_websocketClient is null)
            {
                return Task.CompletedTask;
            }

            long serverTime = Timestamper.Default.UnixTime.MillisecondsLong;
            if (_logger.IsDebug) _logger.Debug("ETH Stats sending 'node-pong' message...");
            return _sender.SendAsync(_websocketClient, new NodePongMessage(clientTime, serverTime), "node-pong");
        }

        private Task SendLatencyFromNodePongAsync(EthStatsNodeTiming? nodeTiming)
        {
            if (_websocketClient is null || nodeTiming?.ClientTime is null)
            {
                return Task.CompletedTask;
            }

            long clientTime = nodeTiming.Value.ClientTime.Value;
            long now = Timestamper.Default.UnixTime.MillisecondsLong;
            if (now < clientTime)
            {
                if (_logger.IsDebug) _logger.Debug($"Ignoring ETH Stats 'node-pong' latency with future clientTime {clientTime}.");
                return Task.CompletedTask;
            }

            long latency = now - clientTime;

            if (_logger.IsDebug) _logger.Debug($"ETH Stats sending 'latency' message after 'node-pong': {latency} ms.");
            return _sender.SendAsync(_websocketClient, new LatencyMessage(latency));
        }

        private Task HandleUnknownMessageAsync(string eventType)
        {
            if (_logger.IsDebug) _logger.Debug($"Ignoring unsupported ETH Stats message '{eventType}'.");
            return Task.CompletedTask;
        }

        internal static bool TryNormalizeHistoryRange(EthStatsHistoryRequest request, out ulong min, out ulong max)
        {
            min = Math.Min(request.Min, request.Max);
            max = Math.Max(request.Min, request.Max);

            if (max < 0)
            {
                min = 0;
                max = 0;
                return false;
            }

            min = Math.Max(0, min);

            if (max - min >= MaxHistoryBlocks)
            {
                min = max - MaxHistoryBlocks + 1;
            }

            return true;
        }

        private static EthStatsBlock CreateBlockModel(CoreBlock block)
        {
            List<EthStatsTransaction> transactions = new(block.Transactions.Length);
            foreach (CoreTransaction transaction in block.Transactions)
            {
                transactions.Add(new EthStatsTransaction((transaction.Hash ?? Keccak.Zero).ToString()));
            }

            List<Uncle> uncles = new(block.Uncles.Length);
            foreach (BlockHeader _ in block.Uncles)
            {
                uncles.Add(new Uncle());
            }

            return new EthStatsBlock(
                block.Number,
                (block.Hash ?? Keccak.Zero).ToString(),
                (block.ParentHash ?? Keccak.Zero).ToString(),
                (long)block.Timestamp,
                (block.Author ?? block.Beneficiary ?? Address.Zero).ToString(),
                block.GasUsed,
                block.GasLimit,
                block.Difficulty.ToString(),
                (block.TotalDifficulty ?? 0).ToString(),
                transactions,
                (block.TxRoot ?? Keccak.Zero).ToString(),
                (block.StateRoot ?? Keccak.Zero).ToString(),
                uncles);
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private Task SendPendingAsync(int pending)
            => _sender.SendAsync(_websocketClient!, new PendingMessage(new PendingStats(pending)));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private async Task SendStatsAsync()
        {
            UInt256 gasPrice = await _gasPriceOracle.GetGasPriceEstimate();
            if (gasPrice > long.MaxValue)
            {
                // EthStats doesn't work with UInt256, long should be enough
                if (_logger.IsTrace) _logger.Trace($"Gas price beyond the eth stats expected scope {gasPrice}");
                gasPrice = long.MaxValue;
            }

            await _sender.SendAsync(_websocketClient!, new StatsMessage(new Messages.Models.Stats(true, _ethSyncingInfo.IsSyncing(), _isMining, 0,
                _peerManager.ActivePeersCount, (long)gasPrice, 100)));
        }
    }
}
