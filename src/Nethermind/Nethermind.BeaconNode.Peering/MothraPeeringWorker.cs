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
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Json;
using Nethermind.Core2.P2p;
using Nethermind.Logging.Microsoft;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public class MothraPeeringWorker : BackgroundService
    {
        private readonly IClientVersion _clientVersion;
        private readonly DataDirectory _dataDirectory;
        private readonly IHostEnvironment _environment;
        private readonly IFileSystem _fileSystem;
        private readonly ForkChoice _forkChoice;
        private readonly JsonSerializerOptions _jsonSerializerOptions;
        private string? _logDirectoryPath;
        private readonly object _logDirectoryPathLock = new object();
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MothraConfiguration> _mothraConfigurationOptions;
        private readonly IMothraLibp2p _mothraLibp2p;
        private readonly PeerManager _peerManager;
        private readonly IStore _store;
        private readonly SynchronizationManager _synchronizationManager;
        private const string MothraDirectory = "mothra";

        public MothraPeeringWorker(ILogger<MothraPeeringWorker> logger,
            IHostEnvironment environment,
            IClientVersion clientVersion,
            DataDirectory dataDirectory,
            IFileSystem fileSystem,
            IOptionsMonitor<MothraConfiguration> mothraConfigurationOptions,
            IMothraLibp2p mothraLibp2p,
            PeerManager peerManager,
            ForkChoice forkChoice,
            SynchronizationManager synchronizationManager,
            IStore store)
        {
            _logger = logger;
            _environment = environment;
            _clientVersion = clientVersion;
            _dataDirectory = dataDirectory;
            _fileSystem = fileSystem;
            _mothraConfigurationOptions = mothraConfigurationOptions;
            _mothraLibp2p = mothraLibp2p;
            _peerManager = peerManager;
            _forkChoice = forkChoice;
            _synchronizationManager = synchronizationManager;
            _store = store;
            _jsonSerializerOptions = new JsonSerializerOptions {WriteIndented = true};
            _jsonSerializerOptions.ConfigureNethermindCore2();
            if (_mothraConfigurationOptions.CurrentValue.LogSignedBeaconBlockJson)
            {
                _ = GetLogDirectory();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_logger.IsDebug()) LogDebug.PeeringWorkerExecute(_logger, null);
                
                await EnsureInitializedWithAnchorState(stoppingToken).ConfigureAwait(false);

                if (_logger.IsDebug()) LogDebug.StoreInitializedStartingPeering(_logger, null);

                _mothraLibp2p.PeerDiscovered += OnPeerDiscovered;
                _mothraLibp2p.GossipReceived += OnGossipReceived;
                _mothraLibp2p.RpcReceived += OnRpcReceived;

                string mothraDataDirectory = Path.Combine(_dataDirectory.ResolvedPath, MothraDirectory);
                MothraSettings mothraSettings = new MothraSettings()
                {
                    DataDirectory = mothraDataDirectory,
                    //Topics = { Topic.BeaconBlock }
                };

                MothraConfiguration mothraConfiguration = _mothraConfigurationOptions.CurrentValue;

                mothraSettings.DiscoveryAddress = mothraConfiguration.DiscoveryAddress;
                mothraSettings.DiscoveryPort = mothraConfiguration.DiscoveryPort;
                mothraSettings.ListenAddress = mothraConfiguration.ListenAddress;
                mothraSettings.MaximumPeers = mothraConfiguration.MaximumPeers;
                mothraSettings.Port = mothraConfiguration.Port;

                foreach (string bootNode in mothraConfiguration.BootNodes)
                {
                    mothraSettings.BootNodes.Add(bootNode);
                    _peerManager.AddExpectedPeer(bootNode);
                }

                if (_logger.IsDebug())
                    LogDebug.MothraStarting(_logger, mothraSettings.ListenAddress, mothraSettings.Port,
                        mothraSettings.BootNodes.Count, null);

                _mothraLibp2p.Start(mothraSettings);

                if (_logger.IsDebug()) LogDebug.PeeringWorkerExecuteCompleted(_logger, null);
            }
            catch (Exception ex)
            {
                if (_logger.IsError()) Log.PeeringWorkerCriticalError(_logger, ex);
            }
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsInfo())
                Log.PeeringWorkerStarting(_logger, _clientVersion.Description,
                    _environment.EnvironmentName, Thread.CurrentThread.ManagedThreadId, null);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.PeeringWorkerStopping(_logger, null);
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureInitializedWithAnchorState(CancellationToken stoppingToken)
        {
            // If the store is not initialized (no anchor block), then can not participate peer-to-peer
            // e.g. can't send status message if we don't have a status.
            // If this is pre-genesis, then the peering will wait until genesis has created the anchor block.
            ulong counter = 0;
            while (!_store.IsInitialized)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1000), stoppingToken).ConfigureAwait(false);
                counter++;
                if (counter % 10 == 0)
                {
                    if (_logger.IsDebug()) LogDebug.PeeringWaitingForAnchorState(_logger, counter, null);
                }
            }

            // Secondary instances wait an additional time, to allow the primary node to initialize
            if (_mothraConfigurationOptions.CurrentValue.BootNodes.Length > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
            }
        }

        private string GetLogDirectory()
        {
            if (_logDirectoryPath == null)
            {
                lock (_logDirectoryPathLock)
                {
                    if (_logDirectoryPath == null)
                    {
                        string basePath = _fileSystem.Path.Combine(_dataDirectory.ResolvedPath, MothraDirectory);
                        IDirectoryInfo baseDirectoryInfo = _fileSystem.DirectoryInfo.FromDirectoryName(basePath);
                        if (!baseDirectoryInfo.Exists)
                        {
                            baseDirectoryInfo.Create();
                        }

                        IDirectoryInfo[] existingLogDirectories = baseDirectoryInfo.GetDirectories("log*");
                        int existingSuffix = existingLogDirectories.Select(x =>
                            {
                                if (int.TryParse(x.Name.Substring(3), out int suffix))
                                {
                                    return suffix;
                                }

                                return 0;
                            })
                            .DefaultIfEmpty()
                            .Max();
                        int newSuffix = existingSuffix + 1;
                        string logDirectoryName = $"log{newSuffix:0000}";

                        if (_logger.IsDebug())
                            LogDebug.CreatingMothraLogDirectory(_logger, logDirectoryName, baseDirectoryInfo.FullName,
                                null);
                        IDirectoryInfo logDirectory = baseDirectoryInfo.CreateSubdirectory(logDirectoryName);
                        _logDirectoryPath = logDirectory.FullName;
                    }
                }
            }

            return _logDirectoryPath;
        }

        private async void HandleBeaconBlockAsync(SignedBeaconBlock signedBeaconBlock)
        {
            try
            {
                if (_mothraConfigurationOptions.CurrentValue.LogSignedBeaconBlockJson)
                {
                    string logDirectoryPath = GetLogDirectory();
                    string fileName = string.Format("signedblock{0:0000}_{1}.json",
                        (int) signedBeaconBlock.Message.Slot, signedBeaconBlock.Signature.ToString().Substring(0, 10));
                    string path = _fileSystem.Path.Combine(logDirectoryPath, fileName);
                    using (Stream fileStream = _fileSystem.File.OpenWrite(path))
                    {
                        await JsonSerializer.SerializeAsync(fileStream, signedBeaconBlock, _jsonSerializerOptions)
                            .ConfigureAwait(false);
                    }
                }

                // Update the most recent slot seen (even if we can't add it to the chain yet, e.g. if we are missing prior blocks)
                // Note: a peer could lie and send a signed block that isn't part of the chain (but it could like on status as well)
                _peerManager.UpdateMostRecentSlot(signedBeaconBlock.Message.Slot);

                await _forkChoice.OnBlockAsync(_store, signedBeaconBlock).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.HandleSignedBeaconBlockError(_logger, signedBeaconBlock.Message, ex.Message, ex);
            }
        }

        private async void HandlePeerDiscoveredAsync(string peerId)
        {
            try
            {
                if (_peerManager.AddPeer(peerId))
                {
                    await _synchronizationManager.OnPeerDialOutConnected(peerId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.HandlePeerDiscoveredError(_logger, peerId, ex.Message, ex);
            }
        }

        private async void HandleRpcStatusAsync(RpcMessage<PeeringStatus> statusRpcMessage)
        {
            try
            {
                PeerDetails peerDetails = _peerManager.UpdatePeerStatus(statusRpcMessage.PeerId, statusRpcMessage.Content);
                // Mothra seems to be raising all incoming RPC (sent as request and as response)
                // with requestResponseFlag 0, so check here if already have the status so we don't go into infinite loop
                // So user peerdetails instead of the status message
                //if (statusRpcMessage.Direction == RpcDirection.Request)
                if (peerDetails.DialDirection == DialDirection.DialOut)
                {
                    // If it is a dial out, we must have already sent the status request and this is the response
                    await _synchronizationManager.OnStatusResponseReceived(statusRpcMessage.PeerId,
                        statusRpcMessage.Content);
                }
                else
                {
                    await _synchronizationManager.OnStatusRequestReceived(statusRpcMessage.PeerId,
                        statusRpcMessage.Content);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.HandleRpcStatusError(_logger, statusRpcMessage.PeerId, ex.Message, ex);
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
                    if (_logger.IsWarn())
                        Log.UnknownGossipReceived(_logger, Encoding.UTF8.GetString(topicUtf8), data.Length, null);
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
            string peerId = Encoding.UTF8.GetString(peerUtf8);
            try
            {
                if (_logger.IsInfo()) Log.PeerDiscovered(_logger, peerId, null);
                if (!ThreadPool.QueueUserWorkItem(HandlePeerDiscoveredAsync, peerId, true))
                {
                    throw new Exception($"Could not queue handling of peer discovered for {peerId}.");
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.PeerDiscoveredError(_logger, peerId, ex.Message, ex);
            }
        }

        private void OnRpcReceived(ReadOnlySpan<byte> methodUtf8, int requestResponseFlag, ReadOnlySpan<byte> peerUtf8,
            ReadOnlySpan<byte> data)
        {
            try
            {
                string peerId = Encoding.UTF8.GetString(peerUtf8);
                RpcDirection rpcDirection = requestResponseFlag == 0 ? RpcDirection.Request : RpcDirection.Response;

                // Even though the value '/eth2/beacon_chain/req/status/1/' is sent, when Mothra calls the received event it is 'HELLO'
                if (methodUtf8.SequenceEqual(MethodUtf8.Status)
                    || methodUtf8.SequenceEqual(MethodUtf8.StatusMothraAlternative))
                {
                    if (_logger.IsDebug())
                        LogDebug.RpcReceived(_logger, rpcDirection, requestResponseFlag, nameof(MethodUtf8.Status), peerId, data.Length, null);

                    PeeringStatus peeringStatus = Ssz.Ssz.DecodePeeringStatus(data);
                    RpcMessage<PeeringStatus> statusRpcMessage =
                        new RpcMessage<PeeringStatus>(peerId, rpcDirection, peeringStatus);
                    if (!ThreadPool.QueueUserWorkItem(HandleRpcStatusAsync, statusRpcMessage, true))
                    {
                        throw new Exception($"Could not queue handling of Status from peer {peerId}.");
                    }
                }
                else
                {
                    // TODO: handle other RPC
                    if (_logger.IsWarn())
                        Log.UnknownRpcReceived(_logger, rpcDirection, requestResponseFlag, Encoding.UTF8.GetString(methodUtf8), peerId,
                            data.Length, null);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.RpcReceivedError(_logger, Encoding.UTF8.GetString(methodUtf8), ex.Message, ex);
            }
        }
    }
}