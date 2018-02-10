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
using System.Threading;
using System.Threading.Tasks;
using Nevermind.Core;
using Nevermind.Discovery.Lifecycle;
using Nevermind.Discovery.RoutingTable;
using Timer = System.Timers.Timer;

namespace Nevermind.Discovery
{
    public class DiscoveryApp : IDiscoveryApp
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodesLocator _nodesLocator;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly INodeFactory _nodeFactory;
        private readonly ILogger _logger;

        private Timer _discoveryTimer;

        public DiscoveryApp(IDiscoveryConfigurationProvider configurationProvider, INodesLocator nodesLocator, ILogger logger, IDiscoveryManager discoveryManager, INodeFactory nodeFactory)
        {
            _configurationProvider = configurationProvider;
            _nodesLocator = nodesLocator;
            _logger = logger;
            _discoveryManager = discoveryManager;
            _nodeFactory = nodeFactory;
        }

        public void Start()
        {
            try
            {
                _logger.Log("Initializing bootNodes.");
                InitializeBootNodes();

                _logger.Log("Starting discovery timer.");
                _discoveryTimer = new Timer(_configurationProvider.DiscoveryInterval);
                _discoveryTimer.Elapsed += async (sender, e) => await RunDiscovery();
                _discoveryTimer.Start();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery app start process", e);
                throw;
            }
        }

        public void Stop()
        {
            try
            {
                _logger.Log("Stopping discovery timer.");
                _discoveryTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery app stop process", e);
                throw;
            }  
        }

        private void InitializeBootNodes()
        {
            var bootNodes = _configurationProvider.BootNodes;
            var managers = new INodeLifecycleManager[bootNodes.Length];
            for (var i = 0; i < bootNodes.Length; i++)
            {
                var bootNode = bootNodes[i];
                var node = _nodeFactory.CreateNode(bootNode.Host, bootNode.Port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers[i] = manager;
            }
            
            //Wait for pong message to come back from Boot nodes
            Thread.Sleep(_configurationProvider.BootNodePongTimeout);

            var reachedNodeCounter = 0;
            for (int i = 0; i < managers.Length; i++)
            {
                var manager = managers[i];
                if (manager.State != NodeLifecycleState.Active)
                {
                    _logger.Log($"Cannot reach boot node: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
                }
                else
                {
                    reachedNodeCounter++;
                }
            }

            if (reachedNodeCounter == 0)
            {
                throw new Exception("Cannot reach any boot nodes. Initialization failed.");
            }
        }

        private async Task RunDiscovery()
        {
            _logger.Log("Running discovery process.");
            await _nodesLocator.LocateNodes();
        }
    }
}