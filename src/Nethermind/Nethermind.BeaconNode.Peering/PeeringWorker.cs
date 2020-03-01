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
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Logging.Microsoft;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public class PeeringWorker : BackgroundService
    {
        private readonly IClientVersion _clientVersion;
        private readonly IConfiguration _configuration;
        private readonly DataDirectory _dataDirectory;
        private readonly IHostEnvironment _environment;
        private readonly ForkChoice _forkChoice;
        private readonly ILogger _logger;
        private const string _mothraDirectory = "mothra";
        private readonly IMothraLibp2p _mothraLibp2p;
        private readonly IOptionsMonitor<PeeringConfiguration> _peeringConfigurationMonitor;
        private readonly IStoreProvider _storeProvider;

        public PeeringWorker(ILogger<PeeringWorker> logger,
            IHostEnvironment environment,
            IConfiguration configuration,
            IClientVersion clientVersion,
            DataDirectory dataDirectory,
            IOptionsMonitor<PeeringConfiguration> peeringConfigurationMonitor,
            IMothraLibp2p mothraLibp2p,
            ForkChoice forkChoice,
            IStoreProvider storeProvider)
        {
            _logger = logger;
            _environment = environment;
            _configuration = configuration;
            _clientVersion = clientVersion;
            _dataDirectory = dataDirectory;
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
                _mothraLibp2p.PeerDiscovered += OnPeerDiscovered;
                _mothraLibp2p.GossipReceived += OnGossipReceived;
                _mothraLibp2p.RpcReceived += OnRpcReceived;

                string mothraDataDirectory = Path.Combine(_dataDirectory.ResolvedPath, _mothraDirectory);
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

        private void HandleBeaconBlock(ReadOnlySpan<byte> data)
        {
            SignedBeaconBlock signedBeaconBlock = Ssz.Ssz.DecodeSignedBeaconBlock(data);
            if (!_storeProvider.TryGetStore(out IStore? retrievedStore))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }

            IStore store = retrievedStore!;
            _forkChoice.OnBlockAsync(store, signedBeaconBlock);
        }

        private void OnGossipReceived(ReadOnlySpan<byte> topicUtf8, ReadOnlySpan<byte> data)
        {
            // TODO: handle topic
            if (topicUtf8.SequenceEqual(TopicUtf8.BeaconBlock))
            {
                if (_logger.IsDebug())
                    LogDebug.GossipReceived(_logger, nameof(TopicUtf8.BeaconBlock), data.Length, null);
                HandleBeaconBlock(data);
            }
            else
            {
                if (_logger.IsDebug()) LogDebug.GossipReceived(_logger, Encoding.UTF8.GetString(topicUtf8), data.Length, null);
            }
        }

        private void OnPeerDiscovered(ReadOnlySpan<byte> peerUtf8)
        {
            if (_logger.IsInfo()) Log.PeerDiscovered(_logger, Encoding.UTF8.GetString(peerUtf8), null);
        }

        private void OnRpcReceived(ReadOnlySpan<byte> methodUtf8, bool isResponse, ReadOnlySpan<byte> peerUtf8, ReadOnlySpan<byte> data)
        {
            if (_logger.IsDebug())
                LogDebug.RpcReceived(_logger, isResponse, Encoding.UTF8.GetString(methodUtf8), Encoding.UTF8.GetString(peerUtf8), data.Length,
                    null);
            // TODO: handle RPC
        }
    }
}