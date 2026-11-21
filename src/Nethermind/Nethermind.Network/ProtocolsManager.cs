// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Network
{
    public class ProtocolsManager : IProtocolsManager, IProtocolRegistrar
    {
        private readonly ConcurrentDictionary<Guid, SyncPeerProtocolHandlerBase> _syncPeers = new();
        private readonly Dictionary<Node, Dictionary<Guid, ProtocolHandlerBase>> _hangingSatelliteProtocols = [];
        private readonly Lock _satelliteProtocolsLock = new();
        private readonly ISyncPeerPool _syncPool;
        private readonly ITxPool _txPool;
        private readonly INodeStatsManager _stats;
        private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IProtocolValidator _protocolValidator;
        private readonly IPeerManager _peerManager;
        private readonly ILogger _logger;
        private readonly IProtocolHandlerFactory[] _factories;
        private readonly IP2PCapabilityResolver[] _capabilityResolvers;
        private readonly Lock _capabilitiesLock = new();
        private Capability[]? _cachedCapabilities;
        private readonly EventHandler<SessionEventArgs> _onSessionCreated;
        private readonly EventHandler<EventArgs> _onSessionInitialized;
        private readonly SessionDisconnectedEventHandler _onSessionDisconnected;

        public ProtocolsManager(
            ISyncPeerPool syncPeerPool,
            ITxPool txPool,
            IDiscoveryApp discoveryApp,
            IRlpxHost rlpxHost,
            INodeStatsManager nodeStatsManager,
            IProtocolValidator protocolValidator,
            IPeerManager peerManager,
            [KeyFilter(DbNames.PeersDb)] INetworkStorage peerStorage,
            IProtocolHandlerFactory[] factories,
            IP2PCapabilityResolver[] capabilityResolvers,
            ILogManager logManager)
        {
            _syncPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _protocolValidator = protocolValidator ?? throw new ArgumentNullException(nameof(protocolValidator));
            _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
            ArgumentNullException.ThrowIfNull(peerStorage);
            _logger = logManager?.GetClassLogger<ProtocolsManager>() ?? throw new ArgumentNullException(nameof(logManager));

            // Order is already set by OrderedComponents<T> (AddFirst/AddLast)
            _factories = factories ?? throw new ArgumentNullException(nameof(factories));
            _capabilityResolvers = capabilityResolvers ?? throw new ArgumentNullException(nameof(capabilityResolvers));
            foreach (IP2PCapabilityResolver resolver in _capabilityResolvers)
            {
                resolver.Changed += InvalidateCapabilities;
            }
            _onSessionCreated = SessionCreated;
            _onSessionInitialized = SessionInitialized;
            _onSessionDisconnected = SessionDisconnected;
            rlpxHost.SessionCreated += _onSessionCreated;
            rlpxHost.SessionDisconnected += _onSessionDisconnected;
        }

        private void SessionCreated(object sender, SessionEventArgs e)
        {
            _sessions.TryAdd(e.Session.SessionId, e.Session);
            e.Session.Initialized += _onSessionInitialized;
        }

        private void SessionDisconnected(object sender, ISession session, DisconnectEventArgs e)
        {
            session.Initialized -= _onSessionInitialized;
            _sessions.TryRemove(session.SessionId, out _);

            Node? node = session.RemoteNodeId is null ? null : session.Node;
            if (_logger.IsTrace && node is not null && session.BestStateReached == SessionState.Initialized)
            {
                TraceSessionDisconnected(session, e);
            }

            if (node is not null)
            {
                lock (_satelliteProtocolsLock)
                {
                    if (_hangingSatelliteProtocols.TryGetValue(node, out Dictionary<Guid, ProtocolHandlerBase>? registrations))
                    {
                        registrations.Remove(session.SessionId);
                        if (registrations.Count == 0) _hangingSatelliteProtocols.Remove(node);
                    }
                }
            }

            if (_syncPeers.TryRemove(session.SessionId, out SyncPeerProtocolHandlerBase? removed) && removed is not null)
            {
                _syncPool.RemovePeer(removed);
                if (removed.IncludeInTxPool) _txPool.RemovePeer(removed);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceSessionDisconnected(ISession session, DisconnectEventArgs e) =>
            _logger.Trace($"{session.Direction} {session.Node.ToString("s"):hide} disconnected {e.DisconnectType} {e.DisconnectReason} {e.Details:hide}");

        private void SessionInitialized(object sender, EventArgs e)
        {
            ISession session = (ISession)sender;
            InitProtocol(session, Protocol.P2P, session.P2PVersion);
        }

        private void InitProtocol(ISession session, string protocolCode, int version)
        {
            if (session.State < SessionState.Initialized)
            {
                throw new InvalidOperationException($"{nameof(InitProtocol)} called on {session}");
            }

            if (session.State != SessionState.Initialized)
            {
                return;
            }

            string code = protocolCode.ToLowerInvariant();

            // Try DI-registered factories in reverse order (plugins registered last take priority)
            for (int i = _factories.Length - 1; i >= 0; i--)
            {
                IProtocolHandlerFactory factory = _factories[i];
                if (factory.ProtocolCode != code) continue;
                if (factory.TryCreate(session, version, out IProtocolHandler? handler))
                {
                    InitHandler(session, handler);
                    return;
                }
            }

            throw new NotSupportedException($"Protocol {code} {version} is not supported");
        }

        private void InitHandler(ISession session, IProtocolHandler handler)
        {
            session.AddProtocolHandler(handler);
            handler.RegisterWith(session, this);
            handler.Init();
        }

        void IProtocolRegistrar.Register(ISession session, ProtocolHandlerBase handler)
        {
        }

        void IProtocolRegistrar.Register(ISession session, P2PProtocolHandler handler)
        {
            session.PingSender = handler;

            Capability[] capabilities = GetAdvertisedCapabilities();
            for (int i = 0; i < capabilities.Length; i++)
            {
                session.AddSupportedCapability(capabilities[i]);
            }
        }

        void IProtocolRegistrar.Register(ISession session, SyncPeerProtocolHandlerBase handler) =>
            session.Node.EthDetails = handler.Name;

        void IProtocolRegistrar.OnProtocolInitialized(ISession session, ProtocolHandlerBase handler, ProtocolInitializedEventArgs args)
        {
            switch (handler)
            {
                case P2PProtocolHandler p2pProtocolHandler:
                    OnP2PProtocolInitialized(session, p2pProtocolHandler, (P2PProtocolInitializedEventArgs)args);
                    break;
                case SyncPeerProtocolHandlerBase syncPeerProtocolHandler:
                    OnSyncPeerProtocolInitialized(session, syncPeerProtocolHandler, (SyncPeerProtocolInitializedEventArgs)args);
                    break;
                default:
                    OnSatelliteProtocolInitialized(session, handler, args);
                    break;
            }
        }

        void IProtocolRegistrar.OnSubprotocolRequested(ISession session, ProtocolHandlerBase handler, string protocolCode, int version) =>
            InitProtocol(session, protocolCode, version);

        private void OnSatelliteProtocolInitialized(ISession session, ProtocolHandlerBase handler, ProtocolInitializedEventArgs args)
        {
            if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;

            bool isValid = _protocolValidator.ValidateOrDisconnect(handler.ProtocolCode, session, args);
            if (isValid)
            {
                PeerInfo? peer = _syncPool.GetPeer(session.Node);
                if (peer is not null)
                {
                    peer.SyncPeer.RegisterSatelliteProtocol(handler.ProtocolCode, handler);
                    if (handler.IsPriority) _syncPool.SetPeerPriority(session.Node.Id);
                    if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode} satellite protocol registered for sync peer {session:hide}.");
                }
                else
                {
                    SyncPeerProtocolHandlerBase? initializingSyncPeer = null;
                    lock (_satelliteProtocolsLock)
                    {
                        if (session.IsClosing) return;
                        foreach ((_, SyncPeerProtocolHandlerBase candidate) in _syncPeers)
                        {
                            if (candidate.Node.Equals(session.Node) && !candidate.Session.IsClosing)
                            {
                                initializingSyncPeer = candidate;
                                break;
                            }
                        }
                        if (initializingSyncPeer is null)
                        {
                            if (!_hangingSatelliteProtocols.TryGetValue(session.Node, out Dictionary<Guid, ProtocolHandlerBase>? registrations))
                            {
                                _hangingSatelliteProtocols.Add(session.Node, registrations = []);
                            }
                            registrations[session.SessionId] = handler;
                        }
                    }

                    if (initializingSyncPeer is not null)
                    {
                        initializingSyncPeer.RegisterSatelliteProtocol(handler.ProtocolCode, handler);
                        if (handler.IsPriority)
                        {
                            initializingSyncPeer.IsPriority = true;
                            _syncPool.SetPeerPriority(session.Node.Id);
                        }
                    }
                    else if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode} satellite protocol sync peer {session:hide} not found.");
                }

                if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session:hide} - adding sync peer {session.Node.ToString("s"):hide}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session:hide}");
            }
        }

        private void OnP2PProtocolInitialized(ISession session, P2PProtocolHandler handler, P2PProtocolInitializedEventArgs args)
        {
            if (!RunBasicChecks(session, Protocol.P2P, handler.ProtocolVersion)) return;
            _peerManager.OnHelloReceived(session);
            if (!_protocolValidator.ValidateOrDisconnect(Protocol.P2P, session, args)) return;

            if (session.Direction == ConnectionDirection.In) session.Node.Port = args.ListenPort;
            _peerManager.OnP2PProtocolInitialized(session);
            if (session.IsClosing) return;

            _stats.ReportP2PInitializationEvent(session.Node, new P2PNodeDetails
            {
                ClientId = args.ClientId,
                Capabilities = args.Capabilities,
                P2PVersion = args.P2PVersion,
                ListenPort = args.ListenPort
            });

            AddNodeToDiscovery(session, args);

            if (_logger.IsTrace) _logger.Trace($"Finalized P2P protocol initialization on {session:hide}");
        }

        private void OnSyncPeerProtocolInitialized(ISession session, SyncPeerProtocolHandlerBase handler, SyncPeerProtocolInitializedEventArgs args)
        {
            if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;

            _stats.ReportSyncPeerInitializeEvent(handler.ProtocolCode, session.Node, new SyncPeerNodeDetails
            {
                NetworkId = args.NetworkId,
                BestHash = args.BestHash,
                GenesisHash = args.GenesisHash,
                ProtocolVersion = args.ProtocolVersion,
                TotalDifficulty = (BigInteger)args.TotalDifficulty
            });
            bool isValid = _protocolValidator.ValidateOrDisconnect(handler.ProtocolCode, session, args);
            if (isValid)
            {
                if (_syncPeers.TryAdd(session.SessionId, handler))
                {
                    ProtocolHandlerBase[] satelliteHandlers;
                    lock (_satelliteProtocolsLock)
                    {
                        satelliteHandlers = _hangingSatelliteProtocols.TryGetValue(handler.Node, out Dictionary<Guid, ProtocolHandlerBase>? registrations)
                            ? registrations.Values.ToArray() : [];
                    }
                    foreach (ProtocolHandlerBase satelliteHandler in satelliteHandlers)
                    {
                        handler.RegisterSatelliteProtocol(satelliteHandler);
                        if (satelliteHandler.IsPriority) handler.IsPriority = true;
                        if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode} satellite protocol registered for sync peer {session:hide}. Sync peer has priority: {handler.IsPriority}");
                    }

                    _syncPool.AddPeer(handler);
                    if (handler.IncludeInTxPool) _txPool.AddPeer(handler);
                    if (_logger.IsTrace) _logger.Trace($"{handler.ClientId:hide} sync peer {session:hide} created.");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Not able to add a sync peer on {session:hide} for {session.Node.ToString("s"):hide}");
                    session.InitiateDisconnect(DisconnectReason.SessionIdAlreadyExists, "sync peer");
                }

                if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session:hide} - adding sync peer {session.Node.ToString("s"):hide}");

            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session:hide}");
            }
        }

        private bool RunBasicChecks(ISession session, string protocolCode, int protocolVersion)
        {
            if (session.IsClosing)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} skipping init, session closing: {session:hide}");
                return false;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session:hide}");
            return true;
        }

        /// <summary>
        /// In case of IN connection we don't know what the port node is listening on is until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(ISession session, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {session:hide}");
                return;
            }

            if (session.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsTrace) _logger.Trace($"Updating listen port for {session:hide} to: {eventArgs.ListenPort}");
                session.Node.Port = eventArgs.ListenPort;
            }

            //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            _discoveryApp.AddNodeToDiscovery(session.Node);
        }

        public int GetHighestProtocolVersion(string protocol)
        {
            int highestVersion = 0;
            foreach (Capability capability in GetAdvertisedCapabilities())
            {
                if (capability.ProtocolCode == protocol)
                {
                    highestVersion = Math.Max(highestVersion, capability.Version);
                }
            }

            return highestVersion;
        }

        private void InvalidateCapabilities()
        {
            lock (_capabilitiesLock)
            {
                _cachedCapabilities = null;
            }
        }

        /// <summary>
        /// Returns the capabilities to advertise, computed by running the registered
        /// <see cref="IP2PCapabilityResolver"/>s (including <see cref="DefaultP2PCapabilityResolver"/>) over an empty
        /// set. The result is cached and only recomputed when a resolver signals a change via
        /// <see cref="IP2PCapabilityResolver.Changed"/>, keeping the per-session path allocation-free.
        /// </summary>
        private Capability[] GetAdvertisedCapabilities()
        {
            Capability[]? cached = Volatile.Read(ref _cachedCapabilities);
            if (cached is not null) return cached;

            lock (_capabilitiesLock)
            {
                if (_cachedCapabilities is null)
                {
                    HashSet<Capability> capabilities = [];
                    foreach (IP2PCapabilityResolver resolver in _capabilityResolvers)
                    {
                        resolver.Resolve(capabilities);
                    }

                    Capability[] resolved = capabilities.ToArray();
                    if (_logger.IsDebug) _logger.Debug($"Resolved advertised P2P capabilities: {string.Join(", ", resolved.Select(static c => $"{c.ProtocolCode}/{c.Version}"))}");
                    Volatile.Write(ref _cachedCapabilities, resolved);
                }

                return _cachedCapabilities!;
            }
        }
    }
}
