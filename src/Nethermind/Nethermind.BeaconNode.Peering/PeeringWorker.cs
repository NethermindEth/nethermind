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
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Logging.Microsoft;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public class PeeringWorker : BackgroundService
    {
        private const string _dataDirectoryKey = "datadirectory";
        private const string _mothraDirectory = "mothra";
        private readonly IClientVersion _clientVersion;
        private readonly IOptionsMonitor<PeeringConfiguration> _peeringConfigurationMonitor;
        private readonly IHostEnvironment _environment;
        private readonly IConfiguration _configuration;
        private readonly ForkChoice _forkChoice;
        private readonly ILogger _logger;
        private readonly IMothraLibp2p _mothraLibp2p;
        private readonly IStoreProvider _storeProvider;

        public PeeringWorker(ILogger<PeeringWorker> logger, IHostEnvironment environment, IConfiguration configuration, IClientVersion clientVersion, IOptionsMonitor<PeeringConfiguration> peeringConfigurationMonitor,
            IMothraLibp2p mothraLibp2p, ForkChoice forkChoice, IStoreProvider storeProvider)
        {
            _logger = logger;
            _environment = environment;
            _configuration = configuration;
            _clientVersion = clientVersion;
            _peeringConfigurationMonitor = peeringConfigurationMonitor;
            _mothraLibp2p = mothraLibp2p;
            _forkChoice = forkChoice;
            _storeProvider = storeProvider;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_logger.IsDebug()) LogDebug.PeeringWorkerExecute(_logger, null);
            return Task.CompletedTask;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsInfo())
                Log.PeeringWorkerStarting(_logger, _clientVersion.Description,
                    _environment.EnvironmentName, Thread.CurrentThread.ManagedThreadId, null);

            try
            {
                _mothraLibp2p.PeerDiscovered += MothraLibp2pOnPeerDiscovered;
                _mothraLibp2p.GossipReceived += MothraLibp2pOnGossipReceived;
                _mothraLibp2p.RpcReceived += MothraLibp2pOnRpcReceived;

                //System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "nethermind/mothra";

                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
                string dataDirectory = _configuration.GetValue<string>(_dataDirectoryKey);
                string mothraDataDirectory = Path.Combine(baseDirectory, dataDirectory, _mothraDirectory);
                MothraSettings mothraSettings = new MothraSettings()
                {
                    DataDirectory = mothraDataDirectory,
                    //Topics = { Topic.BeaconBlock }
                };

                PeeringConfiguration peeringConfiguration = _peeringConfigurationMonitor.CurrentValue;

                mothraSettings.DiscoveryAddress = peeringConfiguration.DiscoveryAddress;
                mothraSettings.DiscoveryPort = peeringConfiguration.DiscoveryPort;
                mothraSettings.ListenAddress = peeringConfiguration.ListenAddress;
                mothraSettings.MaximumPeers = peeringConfiguration.MaximumPeers;
                mothraSettings.Port = peeringConfiguration.Port;

                foreach (string bootNode in peeringConfiguration.BootNodes)
                {
                    mothraSettings.BootNodes.Add(bootNode);
                }

                _mothraLibp2p.Start(mothraSettings);

                if (_logger.IsDebug()) LogDebug.PeeringWorkerStarted(_logger, null);
            }
            catch (Exception ex)
            {
                if (_logger.IsError()) Log.PeeringWorkerCriticalError(_logger, ex);
            }

            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.PeeringWorkerStopping(_logger, null);
            await base.StopAsync(cancellationToken);
        }

        private void HandleBeaconBlock(byte[] data)
        {
            BeaconBlock beaconBlock = Ssz.Ssz.DecodeBeaconBlock(data);
            if (!_storeProvider.TryGetStore(out IStore? retrievedStore))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }

            IStore store = retrievedStore!;
            _forkChoice.OnBlockAsync(store, beaconBlock);
        }

        private void MothraLibp2pOnGossipReceived(object? sender, GossipReceivedEventArgs e)
        {
            if (_logger.IsDebug()) LogDebug.GossipReceived(_logger, e.Topic, e.Data.Length, null);
            // TODO: handle topic
            switch (e.Topic)
            {
                case Topic.BeaconBlock:
                {
                    HandleBeaconBlock(e.Data);
                    break;
                }
            }
        }

        private void MothraLibp2pOnPeerDiscovered(object? sender, PeerDiscoveredEventArgs e)
        {
            if (_logger.IsInfo()) Log.PeerDiscovered(_logger, e.Peer, null);
        }

        private void MothraLibp2pOnRpcReceived(object? sender, RpcReceivedEventArgs e)
        {
            if (_logger.IsDebug())
                LogDebug.RpcReceived(_logger, e.IsResponse ? "Response" : "Request", e.Method, e.Peer, e.Data.Length,
                    null);
            // TODO: handle RPC
        }
    }
}