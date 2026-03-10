// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.NodeData;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.Rlpx;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using ShouldGossip = Nethermind.TxPool.ShouldGossip;

namespace Nethermind.Network
{
    public class ProtocolsManager : IProtocolsManager, IDisposable
    {
        public static readonly IEnumerable<Capability> DefaultCapabilities = new Capability[]
        {
            new(Protocol.Eth, 68),
            new(Protocol.NodeData, 1)
        };

        private readonly ConcurrentDictionary<Guid, SyncPeerProtocolHandlerBase> _syncPeers = new();

        private readonly ConcurrentDictionary<Node, ConcurrentDictionary<Guid, ProtocolHandlerBase>> _hangingSatelliteProtocols =
            new();

        protected readonly ISyncPeerPool _syncPool;
        protected readonly ISyncServer _syncServer;
        protected readonly ITxPool _txPool;
        protected readonly ILogManager _logManager;
        protected readonly ISpecProvider _specProvider;
        protected readonly INodeStatsManager _stats;
        protected readonly IMessageSerializationService _serializer;
        protected readonly ITxGossipPolicy _txGossipPolicy;
        protected readonly IForkInfo _forkInfo;
        protected readonly IGossipPolicy _gossipPolicy;
        protected readonly IBackgroundTaskScheduler _backgroundTaskScheduler;

        private readonly ConcurrentDictionary<Guid, SessionSubscription> _sessionSubscriptions = new();
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<IProtocolHandler, ProtocolHandlerSubscription>> _sessionProtocolSubscriptions = new();
        private readonly Lock _sessionLock = new();
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IRlpxHost _rlpxHost;
        private readonly IProtocolValidator _protocolValidator;
        private readonly INetworkStorage _peerStorage;
        private readonly ITxPoolConfig _txPoolConfig;
        private readonly ILogger _logger;
        private readonly IDictionary<string, Func<ISession, int, IProtocolHandler>> _protocolFactories;
        private readonly HashSet<Capability> _capabilities = DefaultCapabilities.ToHashSet();
        private readonly ISnapServer? _snapServer;
        private readonly EventHandler<ProtocolInitializedEventArgs> _p2pProtocolInitializedHandler;
        private readonly EventHandler<ProtocolInitializedEventArgs> _syncPeerProtocolInitializedHandler;
        private readonly EventHandler<ProtocolInitializedEventArgs> _satelliteProtocolInitializedHandler;
        private volatile bool _disposed;

        public ProtocolsManager(
            ISyncPeerPool syncPeerPool,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ITxPool txPool,
            IDiscoveryApp discoveryApp,
            IMessageSerializationService serializationService,
            IRlpxHost rlpxHost,
            INodeStatsManager nodeStatsManager,
            IProtocolValidator protocolValidator,
            [KeyFilter(DbNames.PeersDb)] INetworkStorage peerStorage,
            IForkInfo forkInfo,
            IGossipPolicy gossipPolicy,
            IWorldStateManager worldStateManager,
            ILogManager logManager,
            ITxPoolConfig txPoolConfig,
            ISpecProvider specProvider,
            ITxGossipPolicy? transactionsGossipPolicy = null)
        {
            ArgumentNullException.ThrowIfNull(syncPeerPool);
            ArgumentNullException.ThrowIfNull(syncServer);
            ArgumentNullException.ThrowIfNull(backgroundTaskScheduler);
            ArgumentNullException.ThrowIfNull(txPool);
            ArgumentNullException.ThrowIfNull(discoveryApp);
            ArgumentNullException.ThrowIfNull(serializationService);
            ArgumentNullException.ThrowIfNull(rlpxHost);
            ArgumentNullException.ThrowIfNull(nodeStatsManager);
            ArgumentNullException.ThrowIfNull(protocolValidator);
            ArgumentNullException.ThrowIfNull(peerStorage);
            ArgumentNullException.ThrowIfNull(forkInfo);
            ArgumentNullException.ThrowIfNull(gossipPolicy);
            ArgumentNullException.ThrowIfNull(worldStateManager);
            ArgumentNullException.ThrowIfNull(logManager);
            ArgumentNullException.ThrowIfNull(txPoolConfig);
            ArgumentNullException.ThrowIfNull(specProvider);

            _syncPool = syncPeerPool;
            _syncServer = syncServer;
            _backgroundTaskScheduler = backgroundTaskScheduler;
            _txPool = txPool;
            _discoveryApp = discoveryApp;
            _serializer = serializationService;
            _rlpxHost = rlpxHost;
            _stats = nodeStatsManager;
            _protocolValidator = protocolValidator;
            _peerStorage = peerStorage;
            _forkInfo = forkInfo;
            _gossipPolicy = gossipPolicy;
            _txGossipPolicy = transactionsGossipPolicy ?? ShouldGossip.Instance;
            _logManager = logManager;
            _txPoolConfig = txPoolConfig;
            _specProvider = specProvider;
            _snapServer = worldStateManager.SnapServer;
            _logger = _logManager.GetClassLogger();

            _protocolFactories = GetProtocolFactories();
            _p2pProtocolInitializedHandler = P2PProtocolInitialized;
            _syncPeerProtocolInitializedHandler = SyncPeerProtocolInitialized;
            _satelliteProtocolInitializedHandler = SatelliteProtocolInitialized;
            _rlpxHost.SessionCreated += SessionCreated;
        }

        private void SessionCreated(object sender, SessionEventArgs e)
        {
            lock (_sessionLock)
            {
                if (_disposed)
                {
                    return;
                }

                SessionSubscription subscription = new(this, e.Session);
                if (_sessionSubscriptions.TryAdd(e.Session.SessionId, subscription))
                {
                    subscription.Attach();
                }
            }
        }

        private void HandleSessionInitialized(ISession session)
        {
            if (_disposed)
            {
                return;
            }

            InitProtocol(session, Protocol.P2P, session.P2PVersion, true);
        }

        private void HandleSessionDisconnected(ISession session, DisconnectEventArgs e)
        {
            RemoveSessionSubscription(session.SessionId);
            RemoveProtocolSubscriptions(session);
            if (_logger.IsDebug && session.BestStateReached == SessionState.Initialized)
            {
                DebugSessionDisconnected();
            }
            RemoveHangingSatelliteRegistration(session);

            PublicKey? syncPeerNodeId = RemoveSyncPeerRegistration(session);
            RemoveTxPoolRegistration(session, syncPeerNodeId);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugSessionDisconnected()
                => _logger.Debug($"{session.Direction} {session.Node:s} disconnected {e.DisconnectType} {e.DisconnectReason} {e.Details}");
        }

        private void InitProtocol(ISession session, string protocolCode, int version, bool addCapabilities = false)
        {
            if (session.State < SessionState.Initialized)
            {
                ThrowInitProtocolCalledOnUninitializedSession(session);
            }

            if (session.State != SessionState.Initialized)
            {
                return;
            }

            IProtocolHandler protocolHandler;
            lock (_sessionLock)
            {
                if (_disposed || session.State != SessionState.Initialized)
                {
                    return;
                }

                if (!_protocolFactories.TryGetValue(protocolCode, out Func<ISession, int, IProtocolHandler>? protocolFactory))
                {
                    ThrowUnsupportedProtocol(protocolCode, version);
                }

                protocolHandler = protocolFactory(session, version);
                TrackProtocolHandler(session, protocolHandler);
                session.AddProtocolHandler(protocolHandler);
                if (addCapabilities)
                {
                    foreach (Capability capability in _capabilities)
                    {
                        session.AddSupportedCapability(capability);
                    }
                }
            }

            protocolHandler.Init();
        }

        public void AddProtocol(string code, Func<ISession, int, IProtocolHandler> factory)
        {
            if (_protocolFactories.ContainsKey(code))
            {
                ThrowProtocolAlreadyAdded(code);
            }

            _protocolFactories[code] = factory;
        }

        protected virtual IDictionary<string, Func<ISession, int, IProtocolHandler>> GetProtocolFactories()
            => new Dictionary<string, Func<ISession, int, IProtocolHandler>>(StringComparer.OrdinalIgnoreCase)
            {
                [Protocol.P2P] = CreateP2PProtocolHandler,
                [Protocol.Eth] = CreateEthProtocolHandler,
                [Protocol.Snap] = CreateSnapProtocolHandler,
                [Protocol.NodeData] = CreateNodeDataProtocolHandler
            };

        private SyncPeerProtocolHandlerBase CreateEthProtocolHandler(ISession session, int version)
        {
            SyncPeerProtocolHandlerBase handler = version switch
            {
                66 => new Eth66ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _forkInfo, _logManager, _txGossipPolicy),
                67 => new Eth67ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _forkInfo, _logManager, _txGossipPolicy),
                68 => new Eth68ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _forkInfo, _logManager, _txPoolConfig, _specProvider, _txGossipPolicy),
                69 => new Eth69ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _forkInfo, _logManager, _txPoolConfig, _specProvider, _txGossipPolicy),
                70 => new Eth70ProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _gossipPolicy, _forkInfo, _logManager, _txPoolConfig, _specProvider, _txGossipPolicy),
                _ => ThrowUnsupportedProtocolVersion<SyncPeerProtocolHandlerBase>(Protocol.Eth, version)
            };
            SetProtocolDetails(handler);
            return handler;
        }

        private ProtocolHandlerBase CreateSnapProtocolHandler(ISession session, int version)
            => CreateSatelliteProtocolHandler(version switch
            {
                1 => new SnapProtocolHandler(session, _stats, _serializer, _backgroundTaskScheduler, _logManager, _snapServer),
                _ => ThrowUnsupportedProtocolVersion<ProtocolHandlerBase>(Protocol.Snap, version)
            });

        private ProtocolHandlerBase CreateNodeDataProtocolHandler(ISession session, int version)
            => CreateSatelliteProtocolHandler(version switch
            {
                1 => new NodeDataProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _logManager),
                _ => ThrowUnsupportedProtocolVersion<ProtocolHandlerBase>(Protocol.NodeData, version)
            });

        private void SatelliteProtocolInitialized(object? sender, ProtocolInitializedEventArgs args)
        {
            if (!TryGetInitializedProtocolHandler<ProtocolHandlerBase>(sender, out ProtocolHandlerBase? handler))
            {
                return;
            }

            ISession session = handler.Session;
            if (!TryValidateInitializedProtocol(session, handler, args))
            {
                return;
            }

            RegisterSatelliteProtocol(session, handler);
            if (_logger.IsTrace) TraceProtocolInitialization(session, handler, ProtocolInitializationTraceEvent.FinalizedSatellite);
        }

        private void P2PProtocolInitialized(object? sender, ProtocolInitializedEventArgs args)
        {
            if (!TryGetInitializedProtocolHandler<P2PProtocolHandler>(sender, out P2PProtocolHandler? handler))
            {
                return;
            }

            ISession session = handler.Session;
            P2PProtocolInitializedEventArgs typedArgs = (P2PProtocolInitializedEventArgs)args;
            if (!RunBasicChecks(session, Protocol.P2P, handler.ProtocolVersion)) return;

            ConfigureSnappy(session, handler);

            _stats.ReportP2PInitializationEvent(session.Node, new P2PNodeDetails
            {
                ClientId = typedArgs.ClientId,
                Capabilities = typedArgs.Capabilities,
                P2PVersion = typedArgs.P2PVersion,
                ListenPort = typedArgs.ListenPort
            });

            AddNodeToDiscovery(session, typedArgs);

            _protocolValidator.DisconnectOnInvalid(Protocol.P2P, session, args);

            if (_logger.IsTrace) TraceP2PProtocolInitializationFinalized();

            void ConfigureSnappy(ISession activeSession, P2PProtocolHandler protocolHandler)
            {
                if (protocolHandler.ProtocolVersion >= 5)
                {
                    if (_logger.IsTrace) TraceSnappyState(isEnabled: true);

                    activeSession.EnableSnappy();
                }
                else if (_logger.IsTrace) TraceSnappyState(isEnabled: false);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceSnappyState(bool isEnabled)
                => _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - {(isEnabled ? "enabling" : "disabling")} snappy");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceP2PProtocolInitializationFinalized()
                => _logger.Trace($"Finalized P2P protocol initialization on {session}");
        }

        private void SyncPeerProtocolInitialized(object? sender, ProtocolInitializedEventArgs args)
        {
            if (!TryGetInitializedProtocolHandler<SyncPeerProtocolHandlerBase>(sender, out SyncPeerProtocolHandlerBase? handler))
            {
                return;
            }

            ISession session = handler.Session;
            if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion))
            {
                return;
            }

            SyncPeerProtocolInitializedEventArgs typedArgs = (SyncPeerProtocolInitializedEventArgs)args;
            _stats.ReportSyncPeerInitializeEvent(handler.ProtocolCode, session.Node, new SyncPeerNodeDetails
            {
                NetworkId = typedArgs.NetworkId,
                BestHash = typedArgs.BestHash,
                GenesisHash = typedArgs.GenesisHash,
                ProtocolVersion = typedArgs.ProtocolVersion,
                TotalDifficulty = (BigInteger)typedArgs.TotalDifficulty
            });

            if (!TryValidateInitializedProtocol(session, handler, args, basicChecksAlreadyRun: true))
            {
                return;
            }

            if (!TryRegisterSyncPeer(session, handler))
            {
                return;
            }

            if (_logger.IsTrace) TraceProtocolInitialization(session, handler, ProtocolInitializationTraceEvent.FinalizedSyncPeer);

            PersistPeer(session);
        }

        private void RemoveHangingSatelliteRegistration(ISession session)
        {
            if (session.Node is not null
                && _hangingSatelliteProtocols.TryGetValue(session.Node, out ConcurrentDictionary<Guid, ProtocolHandlerBase>? registrations)
                && registrations is not null)
            {
                registrations.TryRemove(session.SessionId, out _);
                if (registrations.IsEmpty)
                {
                    _hangingSatelliteProtocols.TryRemove(session.Node, out _);
                }
            }
        }

        private PublicKey? RemoveSyncPeerRegistration(ISession session)
        {
            if (!_syncPeers.TryRemove(session.SessionId, out SyncPeerProtocolHandlerBase? removed) || removed is null)
            {
                return null;
            }

            _syncPool.RemovePeer(removed);
            if (removed.Node?.Id is null)
            {
                return null;
            }

            PublicKey handlerKey = removed.Node.Id;
            _txPool.RemovePeer(handlerKey);
            return handlerKey;
        }

        private void RemoveTxPoolRegistration(ISession session, PublicKey? syncPeerNodeId)
        {
            PublicKey? sessionNodeId = session.Node?.Id;
            if (sessionNodeId is not null && sessionNodeId != syncPeerNodeId)
            {
                _txPool.RemovePeer(sessionNodeId);
            }
        }

        private void RemoveProtocolSubscriptions(ISession session)
        {
            if (!_sessionProtocolSubscriptions.TryRemove(session.SessionId, out ConcurrentDictionary<IProtocolHandler, ProtocolHandlerSubscription>? protocolSubscriptions))
            {
                return;
            }

            foreach ((_, ProtocolHandlerSubscription protocolSubscription) in protocolSubscriptions)
            {
                protocolSubscription.Detach();
            }
        }

        private void TrackProtocolHandler(ISession session, IProtocolHandler protocolHandler)
        {
            ProtocolHandlerSubscription protocolSubscription = CreateProtocolHandlerSubscription(session, protocolHandler);
            protocolSubscription.Attach();

            _sessionProtocolSubscriptions.AddOrUpdate(
                session.SessionId,
                static (_, state) => new ConcurrentDictionary<IProtocolHandler, ProtocolHandlerSubscription>(
                    new[] { new KeyValuePair<IProtocolHandler, ProtocolHandlerSubscription>(state.ProtocolHandler, state.Subscription) }),
                static (_, handlers, state) =>
                {
                    handlers.TryAdd(state.ProtocolHandler, state.Subscription);
                    return handlers;
                },
                (ProtocolHandler: protocolHandler, Subscription: protocolSubscription));
        }

        private P2PProtocolHandler CreateP2PProtocolHandler(ISession session, int _)
        {
            P2PProtocolHandler handler = new(session, _rlpxHost.LocalNodeId, _stats, _serializer, _backgroundTaskScheduler, _logManager);
            session.PingSender = handler;
            return handler;
        }

        private static void SetProtocolDetails(ProtocolHandlerBase handler)
            => handler.Session.Node.EthDetails = handler.Name;

        private static ProtocolHandlerBase CreateSatelliteProtocolHandler(ProtocolHandlerBase handler)
        {
            SetProtocolDetails(handler);
            return handler;
        }

        private bool TryGetInitializedProtocolHandler<THandler>(object? sender, out THandler? handler)
            where THandler : ProtocolHandlerBase
        {
            handler = sender as THandler;
            if (handler is null)
            {
                return false;
            }

            if (_sessionProtocolSubscriptions.TryGetValue(handler.Session.SessionId, out ConcurrentDictionary<IProtocolHandler, ProtocolHandlerSubscription>? protocolSubscriptions) &&
                protocolSubscriptions.TryGetValue(handler, out ProtocolHandlerSubscription? protocolSubscription))
            {
                protocolSubscription.DetachInitializedHandler();
            }

            return true;
        }

        private void RemoveSessionSubscription(Guid sessionId)
        {
            if (_sessionSubscriptions.TryRemove(sessionId, out SessionSubscription? subscription))
            {
                subscription.Detach();
            }
        }

        private ProtocolHandlerSubscription CreateProtocolHandlerSubscription(ISession session, IProtocolHandler protocolHandler)
        {
            EventHandler<ProtocolInitializedEventArgs>? initializedHandler = protocolHandler switch
            {
                P2PProtocolHandler => _p2pProtocolInitializedHandler,
                SyncPeerProtocolHandlerBase => _syncPeerProtocolInitializedHandler,
                ProtocolHandlerBase => _satelliteProtocolInitializedHandler,
                _ => null
            };

            return new ProtocolHandlerSubscription(this, session, protocolHandler, initializedHandler);
        }

        private bool TryValidateInitializedProtocol(ISession session, ProtocolHandlerBase handler, ProtocolInitializedEventArgs args, bool basicChecksAlreadyRun = false)
        {
            if (!basicChecksAlreadyRun && !RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion))
            {
                return false;
            }

            bool isValid = _protocolValidator.DisconnectOnInvalid(handler.ProtocolCode, session, args);
            if (isValid)
            {
                return true;
            }

            if (_logger.IsTrace) TraceProtocolInitialization(session, handler, ProtocolInitializationTraceEvent.Invalid);

            return false;
        }

        private void RegisterSatelliteProtocol(ISession session, ProtocolHandlerBase handler)
        {
            PeerInfo? peerInfo = _syncPool.GetPeer(session.Node);
            if (peerInfo is not null)
            {
                peerInfo.SyncPeer.RegisterSatelliteProtocol(handler.ProtocolCode, handler);
                if (handler.IsPriority)
                {
                    _syncPool.SetPeerPriority(session.Node.Id);
                }

                if (_logger.IsTrace) TraceSatelliteProtocolRegistration(session, handler, SatelliteProtocolRegistrationTraceEvent.Registered);

                return;
            }

            QueueSatelliteProtocolRegistration(session, handler);
            if (_logger.IsTrace) TraceSatelliteProtocolRegistration(session, handler, SatelliteProtocolRegistrationTraceEvent.MissingSyncPeer);
        }

        private void QueueSatelliteProtocolRegistration(ISession session, ProtocolHandlerBase handler)
        {
            _hangingSatelliteProtocols.AddOrUpdate(session.Node,
                static (_, valueTuple) => new ConcurrentDictionary<Guid, ProtocolHandlerBase>(
                    new[] { new KeyValuePair<Guid, ProtocolHandlerBase>(valueTuple.SessionId, valueTuple.Handler) }),
                static (_, dict, valueTuple) =>
                {
                    dict[valueTuple.SessionId] = valueTuple.Handler;
                    return dict;
                },
                (session.SessionId, Handler: handler));
        }

        private bool TryRegisterSyncPeer(ISession session, SyncPeerProtocolHandlerBase handler)
        {
            if (!_syncPeers.TryAdd(session.SessionId, handler))
            {
                if (_logger.IsTrace) TraceSyncPeerRegistrationFailed();

                session.InitiateDisconnect(DisconnectReason.SessionIdAlreadyExists, "sync peer");
                return false;
            }

            RegisterQueuedSatelliteProtocols(session, handler);
            _syncPool.AddPeer(handler);
            if (handler.IncludeInTxPool)
            {
                _txPool.AddPeer(handler);
            }

            if (_logger.IsTrace) TraceSyncPeerCreated();

            return true;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceSyncPeerRegistrationFailed()
                => _logger.Trace($"Not able to add a sync peer on {session} for {session.Node:s}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceSyncPeerCreated()
                => _logger.Trace($"{handler.ClientId} sync peer {session} created.");
        }

        private void RegisterQueuedSatelliteProtocols(ISession session, SyncPeerProtocolHandlerBase handler)
        {
            if (!_hangingSatelliteProtocols.TryGetValue(handler.Node, out ConcurrentDictionary<Guid, ProtocolHandlerBase>? handlerDictionary))
            {
                return;
            }

            foreach (KeyValuePair<Guid, ProtocolHandlerBase> registration in handlerDictionary)
            {
                handler.RegisterSatelliteProtocol(registration.Value);
                if (registration.Value.IsPriority)
                {
                    handler.IsPriority = true;
                }

                if (_logger.IsTrace) TraceSatelliteProtocolRegistration(session, handler, SatelliteProtocolRegistrationTraceEvent.RegisteredWithPriority);
            }
        }

        private void PersistPeer(ISession session)
            => _peerStorage.UpdateNode(new NetworkNode(session.Node.Id, session.Node.Host, session.Node.Port, _stats.GetOrAdd(session.Node).NewPersistedNodeReputation(DateTime.UtcNow)));

        private bool RunBasicChecks(ISession session, string protocolCode, int protocolVersion)
        {
            if (_logger.IsTrace) TraceProtocolInitialized();
            return !session.IsClosing;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceProtocolInitialized()
                => _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
        }

        /// <summary>
        /// In case of IN connection we don't know what is the port node is listening on until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(ISession session, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) TraceNodeDiscovery();

                return;
            }

            if (session.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsTrace) TraceNodeDiscovery(eventArgs.ListenPort);

                session.Node.Port = eventArgs.ListenPort;
            }

            // In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            _discoveryApp.AddNodeToDiscovery(session.Node);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceNodeDiscovery(int? listenPort = null)
                => _logger.Trace(listenPort is null
                    ? $"Listen port is 0, node is not listening: {session}"
                    : $"Updating listen port for {session:s} to: {listenPort}");
        }

        public void AddSupportedCapability(Capability capability)
            => _capabilities.Add(capability);

        public void RemoveSupportedCapability(Capability capability)
        {
            if (_capabilities.Remove(capability))
            {
                if (_logger.IsTrace) TraceSupportedCapabilityRemoved();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceSupportedCapabilityRemoved()
                => _logger.Trace($"Removed supported capability: {capability}");
        }

        public int GetHighestProtocolVersion(string protocol)
        {
            int highestVersion = 0;
            foreach (Capability capability in _capabilities)
            {
                if (capability.ProtocolCode == protocol)
                    highestVersion = Math.Max(highestVersion, capability.Version);
            }

            return highestVersion;
        }

        public void Dispose()
        {
            SessionSubscription[] sessionSubscriptions;
            lock (_sessionLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _rlpxHost.SessionCreated -= SessionCreated;
                sessionSubscriptions = _sessionSubscriptions.Values.ToArray();
                _sessionSubscriptions.Clear();
            }

            foreach (SessionSubscription sessionSubscription in sessionSubscriptions)
            {
                sessionSubscription.Detach();
                RemoveProtocolSubscriptions(sessionSubscription.Session);
            }
        }

        private sealed class SessionSubscription
        {
            private readonly ProtocolsManager _protocolsManager;
            private readonly EventHandler<EventArgs> _onInitialized;
            private readonly EventHandler<DisconnectEventArgs> _onDisconnected;

            public SessionSubscription(ProtocolsManager protocolsManager, ISession session)
            {
                _protocolsManager = protocolsManager;
                Session = session;
                _onInitialized = OnInitialized;
                _onDisconnected = OnDisconnected;
            }

            public ISession Session { get; }

            public void Attach()
            {
                Session.Initialized += _onInitialized;
                Session.Disconnected += _onDisconnected;
            }

            public void Detach()
            {
                Session.Initialized -= _onInitialized;
                Session.Disconnected -= _onDisconnected;
            }

            private void OnInitialized(object? sender, EventArgs e)
                => _protocolsManager.HandleSessionInitialized(Session);

            private void OnDisconnected(object? sender, DisconnectEventArgs e)
                => _protocolsManager.HandleSessionDisconnected(Session, e);
        }

        private sealed class ProtocolHandlerSubscription
        {
            private readonly ProtocolsManager _protocolsManager;
            private readonly ISession _session;
            private readonly IProtocolHandler _protocolHandler;
            private readonly EventHandler<ProtocolInitializedEventArgs>? _initializedHandler;
            private readonly EventHandler<ProtocolEventArgs> _onSubprotocolRequested;

            public ProtocolHandlerSubscription(
                ProtocolsManager protocolsManager,
                ISession session,
                IProtocolHandler protocolHandler,
                EventHandler<ProtocolInitializedEventArgs>? initializedHandler = null)
            {
                _protocolsManager = protocolsManager;
                _session = session;
                _protocolHandler = protocolHandler;
                _initializedHandler = initializedHandler;
                _onSubprotocolRequested = OnSubprotocolRequested;
            }

            public void Attach()
            {
                _protocolHandler.SubprotocolRequested += _onSubprotocolRequested;
                if (_initializedHandler is not null)
                {
                    _protocolHandler.ProtocolInitialized += _initializedHandler;
                }
            }

            public void Detach()
            {
                _protocolHandler.SubprotocolRequested -= _onSubprotocolRequested;
                DetachInitializedHandler();
            }

            public void DetachInitializedHandler()
            {
                if (_initializedHandler is not null)
                {
                    _protocolHandler.ProtocolInitialized -= _initializedHandler;
                }
            }

            private void OnSubprotocolRequested(object? sender, ProtocolEventArgs e)
                => _protocolsManager.InitProtocol(_session, e.ProtocolCode, e.Version);
        }

        private enum SatelliteProtocolRegistrationTraceEvent
        {
            Registered,
            MissingSyncPeer,
            RegisteredWithPriority
        }

        private enum ProtocolInitializationTraceEvent
        {
            Invalid,
            FinalizedSatellite,
            FinalizedSyncPeer
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceSatelliteProtocolRegistration(ISession session, ProtocolHandlerBase handler, SatelliteProtocolRegistrationTraceEvent traceEvent)
        {
            switch (traceEvent)
            {
                case SatelliteProtocolRegistrationTraceEvent.Registered:
                    _logger.Trace($"{handler.ProtocolCode} satellite protocol registered for sync peer {session}.");
                    return;
                case SatelliteProtocolRegistrationTraceEvent.MissingSyncPeer:
                    _logger.Trace($"{handler.ProtocolCode} satellite protocol sync peer {session} not found.");
                    return;
                case SatelliteProtocolRegistrationTraceEvent.RegisteredWithPriority:
                    _logger.Trace($"{handler.ProtocolCode} satellite protocol registered for sync peer {session}. Sync peer has priority: {handler.IsPriority}");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(traceEvent), traceEvent, null);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceProtocolInitialization(ISession session, ProtocolHandlerBase handler, ProtocolInitializationTraceEvent traceEvent)
        {
            switch (traceEvent)
            {
                case ProtocolInitializationTraceEvent.Invalid:
                    _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
                    return;
                case ProtocolInitializationTraceEvent.FinalizedSatellite:
                    _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session}");
                    return;
                case ProtocolInitializationTraceEvent.FinalizedSyncPeer:
                    _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session} - adding sync peer {session.Node:s}");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(traceEvent), traceEvent, null);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInitProtocolCalledOnUninitializedSession(ISession session)
            => throw new InvalidOperationException($"{nameof(InitProtocol)} called on {session}");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowUnsupportedProtocol(string protocolCode, int version)
            => throw new NotSupportedException($"Protocol {protocolCode} {version} is not supported");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowProtocolAlreadyAdded(string code)
            => throw new InvalidOperationException($"Protocol {code} was already added.");

        [DoesNotReturn, StackTraceHidden]
        private static T ThrowUnsupportedProtocolVersion<T>(string protocol, int version)
            => throw new NotSupportedException($"{protocol} protocol version {version} is not supported.");
    }
}
