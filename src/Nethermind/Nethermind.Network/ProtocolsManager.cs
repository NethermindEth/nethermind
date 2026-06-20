// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core.Crypto;
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
        private readonly ConcurrentDictionary<Node, ConcurrentDictionary<Guid, ProtocolHandlerBase>> _hangingSatelliteProtocols = new();
        private readonly ISyncPeerPool _syncPool;
        private readonly ITxPool _txPool;
        private readonly INodeStatsManager _stats;
        private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IProtocolValidator _protocolValidator;
        private readonly INetworkStorage _peerStorage;
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
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _logger = logManager?.GetClassLogger<ProtocolsManager>() ?? throw new ArgumentNullException(nameof(logManager));

            // Order is already set by OrderedComponents<T> (AddFirst/AddLast)
            _factories = factories;
            _capabilityResolvers = capabilityResolvers;
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

            if (_logger.IsDebug && session.BestStateReached == SessionState.Initialized)
            {
                _logger.Debug($"{session.Direction} {session.Node:s} disconnected {e.DisconnectType} {e.DisconnectReason} {e.Details}");
            }

            if (session.Node is not null
                && _hangingSatelliteProtocols.TryGetValue(session.Node, out ConcurrentDictionary<Guid, ProtocolHandlerBase>? registrations)
                && registrations is not null)
            {
                registrations.TryRemove(session.SessionId, out _);
            }

            PublicKey? handlerKey = null;
            if (_syncPeers.TryRemove(session.SessionId, out SyncPeerProtocolHandlerBase? removed) && removed is not null)
            {
                _syncPool.RemovePeer(removed);
                if (removed.Node?.Id is not null)
                {
                    handlerKey = removed.Node.Id;
                    _txPool.RemovePeer(handlerKey);
                }
            }

            PublicKey sessionKey = session.Node?.Id;
            if (sessionKey is not null && sessionKey != handlerKey)
            {
                _txPool.RemovePeer(session.Node.Id);
            }
        }

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

            bool isValid = _protocolValidator.DisconnectOnInvalid(handler.ProtocolCode, session, args);
            if (isValid)
            {
                PeerInfo? peer = _syncPool.GetPeer(session.Node);
                if (peer is not null)
                {
                    peer.SyncPeer.RegisterSatelliteProtocol(handler.ProtocolCode, handler);
                    if (handler.IsPriority) _syncPool.SetPeerPriority(session.Node.Id);
                    if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode} satellite protocol registered for sync peer {session}.");
                }
                else
                {
                    _hangingSatelliteProtocols.AddOrUpdate(session.Node,
                        _ => new ConcurrentDictionary<Guid, ProtocolHandlerBase> { [session.SessionId] = handler },
                        (_, dict) =>
                        {
                            dict[session.SessionId] = handler;
                            return dict;
                        });

                    if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode} satellite protocol sync peer {session} not found.");
                }

                if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session} - adding sync peer {session.Node:s}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
            }
        }

        private void OnP2PProtocolInitialized(ISession session, P2PProtocolHandler handler, P2PProtocolInitializedEventArgs args)
        {
            if (!RunBasicChecks(session, Protocol.P2P, handler.ProtocolVersion)) return;

            if (handler.ProtocolVersion >= 5)
            {
                if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - enabling snappy");
                session.EnableSnappy();
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - disabling snappy");
            }

            _stats.ReportP2PInitializationEvent(session.Node, new P2PNodeDetails
            {
                ClientId = args.ClientId,
                Capabilities = args.Capabilities,
                P2PVersion = args.P2PVersion,
                ListenPort = args.ListenPort
            });

            AddNodeToDiscovery(session, args);

            _protocolValidator.DisconnectOnInvalid(Protocol.P2P, session, args);

            if (_logger.IsTrace) _logger.Trace($"Finalized P2P protocol initialization on {session}");
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
            bool isValid = _protocolValidator.DisconnectOnInvalid(handler.ProtocolCode, session, args);
            if (isValid)
            {
                if (_syncPeers.TryAdd(session.SessionId, handler))
                {
                    if (_hangingSatelliteProtocols.TryGetValue(handler.Node, out ConcurrentDictionary<Guid, ProtocolHandlerBase> handlerDictionary))
                    {
                        foreach (KeyValuePair<Guid, ProtocolHandlerBase> registration in handlerDictionary)
                        {
                            handler.RegisterSatelliteProtocol(registration.Value);
                            if (registration.Value.IsPriority) handler.IsPriority = true;
                            if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode} satellite protocol registered for sync peer {session}. Sync peer has priority: {handler.IsPriority}");
                        }
                    }

                    _syncPool.AddPeer(handler);
                    if (handler.IncludeInTxPool) _txPool.AddPeer(handler);
                    if (_logger.IsTrace) _logger.Trace($"{handler.ClientId} sync peer {session} created.");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Not able to add a sync peer on {session} for {session.Node:s}");
                    session.InitiateDisconnect(DisconnectReason.SessionIdAlreadyExists, "sync peer");
                }

                if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session} - adding sync peer {session.Node:s}");

                //Add/Update peer to the storage and to sync manager
                _peerStorage.UpdateNode(new NetworkNode(session.Node.Id, session.Node.Host, session.Node.Port, _stats.GetOrAdd(session.Node).NewPersistedNodeReputation(DateTime.UtcNow)));
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
            }
        }

        private bool RunBasicChecks(ISession session, string protocolCode, int protocolVersion)
        {
            if (session.IsClosing)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} skipping init, session closing: {session}");
                return false;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
            return true;
        }

        /// <summary>
        /// In case of IN connection we don't know what the port node is listening on is until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(ISession session, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {session}");
                return;
            }

            if (session.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsTrace) _logger.Trace($"Updating listen port for {session:s} to: {eventArgs.ListenPort}");
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

                    Volatile.Write(ref _cachedCapabilities, capabilities.ToArray());
                }

                return _cachedCapabilities;
            }
        }
    }
}
