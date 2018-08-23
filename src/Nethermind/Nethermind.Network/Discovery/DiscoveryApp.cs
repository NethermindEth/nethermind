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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Timer = System.Timers.Timer;

namespace Nethermind.Network.Discovery
{
    public class DiscoveryApp : IDiscoveryApp
    {
        private readonly INetworkConfig _configurationProvider;
        private readonly INodesLocator _nodesLocator;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly INodeFactory _nodeFactory;
        private readonly INodeTable _nodeTable;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly IDiscoveryStorage _discoveryStorage;

        private Timer _discoveryTimer;
        //private Timer _refreshTimer;
        private Timer _discoveryPersistanceTimer;

        private bool _appShutdown;
        private IChannel _channel;
        private MultithreadEventLoopGroup _group;
        private NettyDiscoveryHandler _discoveryHandler;

        public DiscoveryApp(
            INodesLocator nodesLocator,
            IDiscoveryManager discoveryManager,
            INodeFactory nodeFactory,
            INodeTable nodeTable,
            IMessageSerializationService messageSerializationService,
            ICryptoRandom cryptoRandom,
            IDiscoveryStorage discoveryStorage,
            IConfigProvider configurationProvider,
            ILogManager logManager)
        {
            _logManager = logManager;
            _logger = _logManager.GetClassLogger();
            _configurationProvider = configurationProvider.GetConfig<INetworkConfig>();
            _nodesLocator = nodesLocator;
            _discoveryManager = discoveryManager;
            _nodeFactory = nodeFactory;
            _nodeTable = nodeTable;
            _messageSerializationService = messageSerializationService;
            _cryptoRandom = cryptoRandom;
            _discoveryStorage = discoveryStorage;
            _discoveryStorage.StartBatch();
        }

        public void Initialize(PublicKey masterPublicKey)
        {
            // TODO: can we do it so we do not have to call initialize on these classes?
            _nodeTable.Initialize(new NodeId(masterPublicKey));
            _nodesLocator.Initialize(_nodeTable.MasterNode);
        }

        public void Start()
        {
            try
            {
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
            //StopRefreshTimer();
            StopDiscoveryPersistanceTimer();
            await StopUdpChannelAsync();
        }

        private void InitializeUdpChannel()
        {
            if(_logger.IsInfoEnabled) _logger.Info($"Starting Discovery UDP Channel: {_configurationProvider.MasterHost}:{_configurationProvider.MasterPort}");
            _group = new MultithreadEventLoopGroup(1);
            var bootstrap = new Bootstrap();
            bootstrap
                .Group(_group)
                .Channel<SocketDatagramChannel>()
                .Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));

            _bindingTask = bootstrap.BindAsync(IPAddress.Parse(_configurationProvider.MasterHost), _configurationProvider.MasterPort)
                .ContinueWith(t => _channel = t.Result);
        }

        private Task _bindingTask;

        private void InitializeChannel(IDatagramChannel channel)
        {
            _discoveryHandler = new NettyDiscoveryHandler(_discoveryManager, channel, _messageSerializationService, _logManager);
            _discoveryManager.MessageSender = _discoveryHandler;
            _discoveryHandler.OnChannelActivated += OnChannelActivated;
            channel.Pipeline
                .AddLast(new LoggingHandler(DotNetty.Handlers.Logging.LogLevel.INFO))
                .AddLast(_discoveryHandler);
        }

        private void OnChannelActivated(object sender, EventArgs e)
        {
            //Make sure this is non blocking code, otherwise netty will not process messages
            Task.Run(OnChannelActivated).ContinueWith
            (
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Info("Cannot activate channel.");
                        throw t.Exception;
                    }
                    
                    if (t.IsCompleted)
                    {
                        _logger.Info("Discovery App initialized.");
                    }
                }
            );
        }

        private async Task OnChannelActivated()
        {
            try
            {
                //Step 1 - read nodes and stats from db
                AddPersistedNodes();

                //Step 2 - initialize bootNodes
                if(_logger.IsDebugEnabled) _logger.Debug("Initializing bootnodes.");
                while (true)
                {
                    if (await InitializeBootnodes())
                    {
                        break;
                    }
                    
                    _logger.Warn("Could not communicate with any bootnodes.");
                    
                    //Check if we were able to communicate with any trusted nodes or persisted nodes
                    //if so no need to replay bootstraping, we can start discovery process
                    if (_discoveryManager.GetNodeLifecycleManagers(x => x.State == NodeLifecycleState.Active).Any())
                    {
                        break;
                    }
                    
                    _logger.Warn("Could not communicate with any nodes.");
                    await Task.Delay(1000);
                }
                InitializeDiscoveryPersistanceTimer();
                InitializeDiscoveryTimer();

                await RunDiscoveryAsync();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery initialization", e);
            }
        }

        private void AddPersistedNodes()
        {
            if (!_configurationProvider.IsDiscoveryNodesPersistenceOn)
            {
                return;
            }

            var nodes = _discoveryStorage.GetPersistedNodes();
            foreach (var networkNode in nodes)
            {
                var node = _nodeFactory.CreateNode(networkNode.NodeId, networkNode.Host, networkNode.Port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node, true);
                if (manager == null)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"Skiping persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}, manager couldnt be created");
                    }
                    continue;;
                }
                manager.NodeStats.CurrentPersistedNodeReputation = networkNode.Reputation;
                if (_logger.IsTraceEnabled) _logger.Trace($"Adding persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
            }

            if (_logger.IsInfoEnabled) _logger.Info($"Added persisted discovery nodes: {nodes.Length}");
        }

        private void InitializeDiscoveryTimer()
        {
            if(_logger.IsDebugEnabled) _logger.Debug("Starting discovery timer");
            _discoveryTimer = new Timer(_configurationProvider.DiscoveryInterval) {AutoReset = false};
            _discoveryTimer.Elapsed += async (sender, e) =>
            {
                _discoveryTimer.Enabled = false;
                await RunDiscoveryAsync();
                await RunRefreshAsync();
                _discoveryTimer.Enabled = true;
            };
            _discoveryTimer.Start();
        }
        
        private void StopDiscoveryTimer()
        {
            try
            {
                if(_logger.IsDebugEnabled) _logger.Debug("Stopping discovery timer");
                _discoveryTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery timer stop", e);
            }
        }

        private void InitializeDiscoveryPersistanceTimer()
        {
            if(_logger.IsDebugEnabled) _logger.Debug("Starting discovery persistance timer");
            _discoveryPersistanceTimer = new Timer(_configurationProvider.DiscoveryPersistanceInterval) {AutoReset = false};
            _discoveryPersistanceTimer.Elapsed += async (sender, e) =>
            {
                _discoveryPersistanceTimer.Enabled = false;
                await Task.Run(() => RunDiscoveryCommit()); 
                _discoveryPersistanceTimer.Enabled = true;
            };
            _discoveryPersistanceTimer.Start();
        }

        private void StopDiscoveryPersistanceTimer()
        {
            try
            {
                if(_logger.IsDebugEnabled) _logger.Debug("Stopping discovery persistance timer");
                _discoveryPersistanceTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery persistance timer stop", e);
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
            var bootNodes = _configurationProvider.BootNodes;
            if (bootNodes == null || !bootNodes.Any())
            {
                if (_logger.IsWarnEnabled) _logger.Warn("No bootnodes specified in configuration");
                return true;
            }
            
            var managers = new List<INodeLifecycleManager>();
            for (var i = 0; i < bootNodes.Length; i++)
            {
                var bootnode = bootNodes[i];
                var node = bootnode.NodeId == null
                    ? _nodeFactory.CreateNode(bootnode.Host, bootnode.Port)
                    : _nodeFactory.CreateNode(new NodeId(new PublicKey(Bytes.FromHexString(bootnode.NodeId))), bootnode.Host, bootnode.Port, true);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                if (manager != null)
                {
                    managers.Add(manager);
                }
                else
                {
                    _logger.Warn($"Bootnode config contains self: {bootnode.NodeId}");
                }
            }

            // TODO: strange sync - can we just have a timeout within which we expect to be notified about an added active manager?
            // TODO: Task.WhenAny with delay should do
            //Wait for pong message to come back from Boot nodes
            var maxWaitTime = _configurationProvider.BootNodePongTimeout;
            var itemTime = maxWaitTime / 100;
            for (var i = 0; i < 100; i++)
            {
                if (managers.Any(x => x.State == NodeLifecycleState.Active))
                {
                    break;
                }

                if (_logger.IsTraceEnabled) _logger.Trace($"Waiting {itemTime} ms for bootnodes to respond");
                await Task.Delay(1000); // TODO: do we need this?
            }

            var reachedNodeCounter = 0;
            for (var i = 0; i < managers.Count; i++)
            {
                var manager = managers[i];
                if (manager.State != NodeLifecycleState.Active)
                {
                    if (_logger.IsDebugEnabled) _logger.Debug($"Could not reach bootnode: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
                }
                else
                {
                    if (_logger.IsDebugEnabled) _logger.Debug($"Reached bootnode: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
                    reachedNodeCounter++;
                }
            }

            if (_logger.IsInfoEnabled) _logger.Info($"Connected to {reachedNodeCounter} bootnodes");
            return reachedNodeCounter > 0;
        }

        private async Task RunDiscoveryAsync()
        {
            if (_logger.IsDebugEnabled) _logger.Debug("Running discovery process.");
            await _nodesLocator.LocateNodesAsync();
        }

        private async Task RunRefreshAsync()
        {
            if (_logger.IsDebugEnabled) _logger.Debug("Running refresh process.");            
            var randomId = _cryptoRandom.GenerateRandomBytes(64);
            await _nodesLocator.LocateNodesAsync(randomId);
        }

        private void RunDiscoveryCommit()
        {
            var managers = _discoveryManager.GetNodeLifecycleManagers();
            //we need to update all notes to update reputation
            _discoveryStorage.UpdateNodes(managers.Select(x => new NetworkNode(x.ManagedNode.Id.PublicKey, x.ManagedNode.Host, x.ManagedNode.Port, x.ManagedNode.Description, x.NodeStats.NewPersistedNodeReputation)).ToArray());

            if (!_discoveryStorage.AnyPendingChange())
            {
                return;
            }

            _discoveryStorage.Commit();
            _discoveryStorage.StartBatch();
        }
    }
}