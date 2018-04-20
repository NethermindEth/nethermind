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
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;
using Timer = System.Timers.Timer;

namespace Nethermind.Discovery
{
    public class DiscoveryApp : IDiscoveryApp
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodesLocator _nodesLocator;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly INodeFactory _nodeFactory;
        private readonly INodeTable _nodeTable;
        private readonly ILogger _logger;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ICryptoRandom _cryptoRandom;

        private Timer _discoveryTimer;
        private Timer _refreshTimer;
        private bool _appShutdown;
        private IChannel _channel;
        private MultithreadEventLoopGroup _group;
        private NettyDiscoveryHandler _discoveryHandler;

        public DiscoveryApp(IDiscoveryConfigurationProvider configurationProvider, INodesLocator nodesLocator, ILogger logger, IDiscoveryManager discoveryManager, INodeFactory nodeFactory, INodeTable nodeTable, IMessageSerializationService messageSerializationService, ICryptoRandom cryptoRandom)
        {
            _configurationProvider = configurationProvider;
            _nodesLocator = nodesLocator;
            _logger = logger;
            _discoveryManager = discoveryManager;
            _nodeFactory = nodeFactory;
            _nodeTable = nodeTable;
            _messageSerializationService = messageSerializationService;
            _cryptoRandom = cryptoRandom;
        }

        public void Start(PublicKey masterPublicKey)
        {
            try
            {
                // TODO: can we do it so we do not have to call initialize on these classes?
                _nodeTable.Initialize(masterPublicKey);
                _nodesLocator.Initialize(_nodeTable.MasterNode);
                _logger.Info("Initializing UDP channel.");
                InitializeUdpChannel();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery app start process", e);
                throw;
            }
        }

        public async Task StopAsync()
        {
            _appShutdown = true;
            StopDiscoveryTimer();
            StopRefreshTimer();
            await StopUdpChannelAsync();
        }

        private void InitializeUdpChannel()
        {
            _group = new MultithreadEventLoopGroup(1);
            //try
            //{
            //while (!_appShutdown)
            //{
            StartUdpChannel();
            //wait for closing event
            //await _channel.CloseCompletion;
            //StopUdpChannelAsync();
            //}
            //}
            //finally 
            //{
            //    await _group.ShutdownGracefullyAsync();
            //}
        }

        private void StartUdpChannel()
        {
            //var address = new IPEndPoint(IPAddress.Parse(_configurationProvider.MasterHost), _configurationProvider.MasterPort);
            //var address = _nodeTable.MasterNode.Address;
            _logger.Info($"Starting UDP Channel: {_configurationProvider.MasterHost}:{_configurationProvider.MasterPort}");

            var bootstrap = new Bootstrap();
            bootstrap
                .Group(_group)
                .Channel<SocketDatagramChannel>()
                .Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));

            _bindingTask = bootstrap.BindAsync(IPAddress.Parse(_configurationProvider.MasterHost), _configurationProvider.MasterPort).ContinueWith(t => _channel = t.Result);
        }

        private Task _bindingTask;

        private void InitializeChannel(IDatagramChannel channel)
        {
            _discoveryHandler = new NettyDiscoveryHandler(_logger, _discoveryManager, channel, _messageSerializationService);
            _discoveryManager.MessageSender = _discoveryHandler;
            _discoveryHandler.OnChannelActivated += OnChannelActivated;
            channel.Pipeline
                .AddLast(new LoggingHandler(DotNetty.Handlers.Logging.LogLevel.INFO))
                .AddLast(_discoveryHandler);
        }

        private void OnChannelActivated(object sender, EventArgs e)
        {
            OnChannelActivated().ContinueWith
            (
                t =>
                {
                    if (t.IsFaulted)
                    {
                        throw t.Exception;
                    }
                    
                    if (t.IsCompleted)
                    {
                        _logger.Info("Bootnodes initialized.");
                    }
                }
            );
        }

        private async Task OnChannelActivated()
        {
            try
            {
                _logger.Info("Initializing bootnodes.");
                if (!await InitializeBootnodes())
                {
                    _logger.Error("Could not communicate with any bootnodes. Initialization failed.");
                    return;
                }

                await RunDiscoveryAsync();

                //InitializeDiscoveryTimer();
                //InitializeRefreshTimer();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery initialization", e);
            }
        }

        private void InitializeDiscoveryTimer()
        {
            _logger.Info("Starting discovery timer");
            _discoveryTimer = new Timer(_configurationProvider.DiscoveryInterval);
            _discoveryTimer.Elapsed += async (sender, e) => await RunDiscoveryAsync();
            _discoveryTimer.Start();
        }

        private void StopDiscoveryTimer()
        {
            try
            {
                _logger.Info("Stopping discovery timer");
                _discoveryTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery timer stop", e);
            }
        }

        private void InitializeRefreshTimer()
        {
            _logger.Info("Starting refresh timer");
            _refreshTimer = new Timer(_configurationProvider.RefreshInterval);
            _refreshTimer.Elapsed += async (sender, e) => await RunRefreshAsync();
            _refreshTimer.Start();
        }

        private void StopRefreshTimer()
        {
            try
            {
                _logger.Info("Stopping refresh timer");
                _refreshTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during refresh timer stop", e);
            }
        }

        private async Task StopUdpChannelAsync()
        {
            try
            {
                await _bindingTask; // if we are still starting

                _logger.Info("Stopping udp channel");
                var closeTask = _channel.CloseAsync();
                if (await Task.WhenAny(closeTask, Task.Delay(_configurationProvider.UdpChannelCloseTimeout)) != closeTask)
                {
                    _logger.Error($"Could not close udp connection in {_configurationProvider.UdpChannelCloseTimeout} miliseconds");
                }

                if (_discoveryManager != null)
                {
                    _discoveryHandler.OnChannelActivated -= OnChannelActivated;
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error during udp channel stop process", e);
            }
        }

        private async Task<bool> InitializeBootnodes()
        {
            var bootNodes = _configurationProvider.Bootnodes;
            var managers = new INodeLifecycleManager[bootNodes.Length];
            for (var i = 0; i < bootNodes.Length; i++)
            {
                var bootnode = bootNodes[i];
                var node = bootnode.PublicKey == null
                    ? _nodeFactory.CreateNode(bootnode.Host, bootnode.Port)
                    : _nodeFactory.CreateNode(bootnode.PublicKey, bootnode.Host, bootnode.Port, true);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers[i] = manager;
            }

            //Wait for pong message to come back from Boot nodes
            var maxWaitTime = _configurationProvider.BootNodePongTimeout;
            var itemTime = maxWaitTime / 100;
            for (var i = 0; i < 100; i++)
            {
                if (managers.Any(x => x.State == NodeLifecycleState.Active))
                {
                    break;
                }

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Waiting {itemTime} ms for bootnodes to respond");
                }

                await Task.Delay(1000);
            }

            var reachedNodeCounter = 0;
            for (var i = 0; i < managers.Length; i++)
            {
                var manager = managers[i];
                if (manager.State != NodeLifecycleState.Active)
                {
                    if (_logger.IsWarnEnabled)
                    {
                        _logger.Warn($"Cannot reach bootnode: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
                    }
                }
                else
                {
                    reachedNodeCounter++;
                }
            }

            return reachedNodeCounter > 0;
        }

        private async Task RunDiscoveryAsync()
        {
            _logger.Info("Running discovery process.");
            await _nodesLocator.LocateNodesAsync();
        }

        private async Task RunRefreshAsync()
        {
            _logger.Info("Running refresh process.");
            var randomId = _cryptoRandom.GenerateRandomBytes(64);
            await _nodesLocator.LocateNodesAsync(randomId);
        }
    }
}