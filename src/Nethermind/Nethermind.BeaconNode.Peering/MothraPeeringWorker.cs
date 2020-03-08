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
    public class MothraPeeringWorker : BackgroundService
    {
        private readonly IClientVersion _clientVersion;
        private readonly IConfiguration _configuration;
        private readonly DataDirectory _dataDirectory;
        private readonly IHostEnvironment _environment;
        private readonly ForkChoice _forkChoice;
        private readonly ILogger _logger;
        private const string _mothraDirectory = "mothra";
        private readonly IMothraLibp2p _mothraLibp2p;
        private readonly IOptionsMonitor<MothraConfiguration> _mothraConfigurationMonitor;
        private readonly IStore _store;

        public MothraPeeringWorker(ILogger<MothraPeeringWorker> logger,
            IHostEnvironment environment,
            IConfiguration configuration,
            IClientVersion clientVersion,
            DataDirectory dataDirectory,
            IOptionsMonitor<MothraConfiguration> mothraConfigurationMonitor,
            IMothraLibp2p mothraLibp2p,
            ForkChoice forkChoice,
            IStore store)
        {
            _logger = logger;
            _environment = environment;
            _configuration = configuration;
            _clientVersion = clientVersion;
            _dataDirectory = dataDirectory;
            _mothraConfigurationMonitor = mothraConfigurationMonitor;
            _mothraLibp2p = mothraLibp2p;
            _forkChoice = forkChoice;
            _store = store;
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

                MothraConfiguration mothraConfiguration = _mothraConfigurationMonitor.CurrentValue;

                mothraSettings.DiscoveryAddress = mothraConfiguration.DiscoveryAddress;
                mothraSettings.DiscoveryPort = mothraConfiguration.DiscoveryPort;
                mothraSettings.ListenAddress = mothraConfiguration.ListenAddress;
                mothraSettings.MaximumPeers = mothraConfiguration.MaximumPeers;
                mothraSettings.Port = mothraConfiguration.Port;

                foreach (string bootNode in mothraConfiguration.BootNodes)
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

        private async void HandleBeaconBlockAsync(SignedBeaconBlock signedBeaconBlock)
        {
            try
            {
                await _forkChoice.OnBlockAsync(_store, signedBeaconBlock).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.HandleSignedBeaconBlockError(_logger, signedBeaconBlock.Message, ex.Message, ex);
            }
        }

        private void OnGossipReceived(ReadOnlySpan<byte> topicUtf8, ReadOnlySpan<byte> data)
        {
            try
            {
                // TODO: handle other topics
                if (topicUtf8.SequenceEqual(TopicUtf8.BeaconBlock))
                {
                    if (_logger.IsDebug())
                        LogDebug.GossipReceived(_logger, nameof(TopicUtf8.BeaconBlock), data.Length, null);
                    // Need to deserialize in synchronous context (can't pass Span async)
                    SignedBeaconBlock signedBeaconBlock = Ssz.Ssz.DecodeSignedBeaconBlock(data);
                    // TODO: maybe use a blocking queue that the receiver converts and writes to, then the async worker (ExcecuteAsync) can process.
                    if (!ThreadPool.QueueUserWorkItem(HandleBeaconBlockAsync, signedBeaconBlock, true))
                    {
                        throw new Exception($"Could not queue handling of block {signedBeaconBlock.Message}.");
                    }
                }
                else
                {
                    if (_logger.IsDebug())
                        LogDebug.GossipReceived(_logger, Encoding.UTF8.GetString(topicUtf8), data.Length, null);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.GossipReceivedError(_logger, Encoding.UTF8.GetString(topicUtf8), ex.Message, ex);
            }
        }

        private void OnPeerDiscovered(ReadOnlySpan<byte> peerUtf8)
        {
            try
            {
                if (_logger.IsInfo()) Log.PeerDiscovered(_logger, Encoding.UTF8.GetString(peerUtf8), null);
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.PeerDiscoveredError(_logger, Encoding.UTF8.GetString(peerUtf8), ex.Message, ex);
            }
        }

        private void OnRpcReceived(ReadOnlySpan<byte> methodUtf8, bool isResponse, ReadOnlySpan<byte> peerUtf8, ReadOnlySpan<byte> data)
        {
            try
            {
                if (_logger.IsDebug())
                    LogDebug.RpcReceived(_logger, isResponse, Encoding.UTF8.GetString(methodUtf8), Encoding.UTF8.GetString(peerUtf8), data.Length,
                        null);
                // TODO: handle RPC
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.RpcReceivedError(_logger, Encoding.UTF8.GetString(methodUtf8), ex.Message, ex);
            }
        }
    }
}