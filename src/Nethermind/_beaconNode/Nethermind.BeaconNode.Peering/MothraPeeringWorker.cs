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
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;
using Nethermind.Peering.Mothra;

namespace Nethermind.BeaconNode.Peering
{
    public class MothraPeeringWorker : BackgroundService
    {
        private readonly IClientVersion _clientVersion;
        private readonly DataDirectory _dataDirectory;
        private readonly IHostEnvironment _environment;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MothraConfiguration> _mothraConfigurationOptions;
        private readonly IMothraLibp2p _mothraLibp2P;
        private readonly PeerDiscoveredProcessor _peerDiscoveredProcessor;
        private readonly PeerManager _peerManager;
        private readonly RpcBeaconBlocksByRangeProcessor _rpcBeaconBlocksByRangeProcessor;
        private readonly RpcPeeringStatusProcessor _rpcPeeringStatusProcessor;
        private readonly SignedBeaconBlockProcessor _signedBeaconBlockProcessor;
        private readonly IStore _store;
        internal const string MothraDirectory = "mothra";
        private int _minimumSignedBeaconBlockLength;
        
        public MothraPeeringWorker(ILogger<MothraPeeringWorker> logger,
            IOptionsMonitor<MothraConfiguration> mothraConfigurationOptions,
            IHostEnvironment environment,
            IClientVersion clientVersion,
            IStore store,
            IMothraLibp2p mothraLibp2P,
            DataDirectory dataDirectory,
            PeerManager peerManager,
            PeerDiscoveredProcessor peerDiscoveredProcessor,
            RpcPeeringStatusProcessor rpcPeeringStatusProcessor,
            RpcBeaconBlocksByRangeProcessor rpcBeaconBlocksByRangeProcessor,
            SignedBeaconBlockProcessor signedBeaconBlockProcessor)
        {
            _logger = logger;
            _environment = environment;
            _clientVersion = clientVersion;
            _dataDirectory = dataDirectory;
            _mothraConfigurationOptions = mothraConfigurationOptions;
            _mothraLibp2P = mothraLibp2P;
            _peerManager = peerManager;
            _peerDiscoveredProcessor = peerDiscoveredProcessor;
            _rpcPeeringStatusProcessor = rpcPeeringStatusProcessor;
            _rpcBeaconBlocksByRangeProcessor = rpcBeaconBlocksByRangeProcessor;
            _signedBeaconBlockProcessor = signedBeaconBlockProcessor;
            _store = store;
            
            // 396 bytes
            _minimumSignedBeaconBlockLength = Ssz.Ssz.SignedBeaconBlockLength(
                new SignedBeaconBlock(new BeaconBlock(Slot.Zero, Root.Zero, Root.Zero, BeaconBlockBody.Zero),
                    BlsSignature.Zero));
            
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_logger.IsDebug()) LogDebug.PeeringWorkerExecute(_logger, null);

                await EnsureInitializedWithAnchorState(stoppingToken).ConfigureAwait(false);

                if (_logger.IsDebug()) LogDebug.StoreInitializedStartingPeering(_logger, null);

                await _peerDiscoveredProcessor.StartAsync(stoppingToken).ConfigureAwait(false);
                await _rpcPeeringStatusProcessor.StartAsync(stoppingToken).ConfigureAwait(false);
                await _rpcBeaconBlocksByRangeProcessor.StartAsync(stoppingToken).ConfigureAwait(false);
                await _signedBeaconBlockProcessor.StartAsync(stoppingToken).ConfigureAwait(false);

                _mothraLibp2P.PeerDiscovered += OnPeerDiscovered;
                _mothraLibp2P.GossipReceived += OnGossipReceived;
                _mothraLibp2P.RpcReceived += OnRpcReceived;

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

                _mothraLibp2P.Start(mothraSettings);

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
            await _peerDiscoveredProcessor.StopAsync(cancellationToken).ConfigureAwait(false);
            await _rpcPeeringStatusProcessor.StopAsync(cancellationToken).ConfigureAwait(false);
            await _rpcBeaconBlocksByRangeProcessor.StopAsync(cancellationToken).ConfigureAwait(false);
            await _signedBeaconBlockProcessor.StopAsync(cancellationToken).ConfigureAwait(false);

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

        private void OnGossipReceived(ReadOnlySpan<byte> topicUtf8, ReadOnlySpan<byte> data)
        {
            Activity activity = new Activity("gossip-received");
            activity.Start();
            using var activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            try
            {
                // TODO: handle other topics
                if (topicUtf8.SequenceEqual(TopicUtf8.BeaconBlock))
                {
                    if (_logger.IsDebug())
                        LogDebug.GossipReceived(_logger, Encoding.UTF8.GetString(topicUtf8), data.Length, null);
                    // Need to deserialize in synchronous context (can't pass Span async)
                    SignedBeaconBlock signedBeaconBlock = Ssz.Ssz.DecodeSignedBeaconBlock(data);
                    _signedBeaconBlockProcessor.EnqueueGossip(signedBeaconBlock);

                    // TODO: After receiving a gossip, should we re-gossip it, i.e. to our peers?
                    // NOTE: Need to apply validations, from spec, before forwarding; also, avoid loops (i.e. don't gossip twice)
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
            finally
            {
                activity.Stop();
            }
        }

        private void OnPeerDiscovered(ReadOnlySpan<byte> peerUtf8)
        {
            Activity activity = new Activity("discovered-peer");
            activity.Start();
            using var activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            string peerId = Encoding.UTF8.GetString(peerUtf8);
            try
            {
                if (_logger.IsInfo()) Log.PeerDiscovered(_logger, peerId, null);
                _peerDiscoveredProcessor.Enqueue(peerId);
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.PeerDiscoveredError(_logger, peerId, ex.Message, ex);
            }
            finally
            {
                activity.Stop();
            }
        }

        private void OnRpcReceived(ReadOnlySpan<byte> methodUtf8, int requestResponseFlag, ReadOnlySpan<byte> peerUtf8,
            ReadOnlySpan<byte> data)
        {
            Activity activity = new Activity("rpc-received");
            activity.Start();
            using var activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            try
            {
                string peerId = Encoding.UTF8.GetString(peerUtf8);
                RpcDirection rpcDirection = requestResponseFlag == 0 ? RpcDirection.Request : RpcDirection.Response;

                // Even though the value '/eth2/beacon_chain/req/status/1/' is sent, when Mothra calls the received event it is 'HELLO'
                // if (methodUtf8.SequenceEqual(MethodUtf8.Status)
                //     || methodUtf8.SequenceEqual(MethodUtf8.StatusMothraAlternative))
                if (data.Length == Ssz.Ssz.PeeringStatusLength)
                {
                    if (_logger.IsDebug())
                        LogDebug.RpcReceived(_logger, rpcDirection, requestResponseFlag,
                            Encoding.UTF8.GetString(methodUtf8),
                            peerId, data.Length, nameof(MethodUtf8.Status), null);

                    PeeringStatus peeringStatus = Ssz.Ssz.DecodePeeringStatus(data);
                    RpcMessage<PeeringStatus> statusRpcMessage =
                        new RpcMessage<PeeringStatus>(peerId, rpcDirection, peeringStatus);
                    _rpcPeeringStatusProcessor.Enqueue(statusRpcMessage);
                }
                //else if (methodUtf8.SequenceEqual(MethodUtf8.BeaconBlocksByRange))
                else if (data.Length == Ssz.Ssz.BeaconBlocksByRangeLength ||
                         data.Length >= _minimumSignedBeaconBlockLength)
                {
                    if (_logger.IsDebug())
                        LogDebug.RpcReceived(_logger, rpcDirection, requestResponseFlag,
                            Encoding.UTF8.GetString(methodUtf8),
                            peerId, data.Length, nameof(MethodUtf8.BeaconBlocksByRange), null);

                    //if (rpcDirection == RpcDirection.Request)
                    if (data.Length == Ssz.Ssz.BeaconBlocksByRangeLength)
                    {
                        BeaconBlocksByRange beaconBlocksByRange = Ssz.Ssz.DecodeBeaconBlocksByRange(data);
                        RpcMessage<BeaconBlocksByRange> rpcMessage =
                            new RpcMessage<BeaconBlocksByRange>(peerId, rpcDirection, beaconBlocksByRange);
                        _rpcBeaconBlocksByRangeProcessor.Enqueue(rpcMessage);
                    }
                    else
                    {
                        SignedBeaconBlock signedBeaconBlock = Ssz.Ssz.DecodeSignedBeaconBlock(data);
                        _signedBeaconBlockProcessor.Enqueue(signedBeaconBlock, peerId);
                    }
                }
                else
                {
                    // TODO: handle other RPC
                    if (_logger.IsWarn())
                        Log.UnknownRpcReceived(_logger, rpcDirection, requestResponseFlag,
                            Encoding.UTF8.GetString(methodUtf8), peerId,
                            data.Length, null);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.RpcReceivedError(_logger, Encoding.UTF8.GetString(methodUtf8), ex.Message, ex);
            }
            finally
            {
                activity.Stop();
            }
        }
    }
}