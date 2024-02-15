// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.P2P.Subprotocols.NodeData;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Wit;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using ShouldGossip = Nethermind.TxPool.ShouldGossip;

namespace Nethermind.Network
{
    public class ProtocolsManager : IProtocolsManager
    {
        public const int AnyVersion = -1;

        private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();
        private readonly ISyncPeerPool _syncPool;
        private readonly ISyncServer _syncServer;
        private readonly ITxPool _txPool;
        private readonly IPooledTxsRequestor _pooledTxsRequestor;
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IMessageSerializationService _serializer;
        private readonly IRlpxHost _rlpxHost;
        private readonly INodeStatsManager _stats;
        private readonly IProtocolValidator _protocolValidator;
        private readonly INetworkStorage _peerStorage;
        private readonly ForkInfo _forkInfo;
        private readonly IGossipPolicy _gossipPolicy;
        private readonly ITxGossipPolicy _txGossipPolicy;
        private readonly INetworkConfig _networkConfig;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IDictionary<(string, int), IProtocolHandlerFactory> _protocolFactories;
        private readonly HashSet<Capability> _capabilities = new();
        private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;

        public ProtocolsManager(
            ISyncPeerPool syncPeerPool,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            IDiscoveryApp discoveryApp,
            IMessageSerializationService serializationService,
            IRlpxHost rlpxHost,
            INodeStatsManager nodeStatsManager,
            IProtocolValidator protocolValidator,
            INetworkStorage peerStorage,
            ForkInfo forkInfo,
            IGossipPolicy gossipPolicy,
            INetworkConfig networkConfig,
            ILogManager logManager,
            ITxGossipPolicy? transactionsGossipPolicy = null)
        {
            _syncPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _backgroundTaskScheduler = backgroundTaskScheduler ?? throw new ArgumentNullException(nameof(backgroundTaskScheduler));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _pooledTxsRequestor = pooledTxsRequestor ?? throw new ArgumentNullException(nameof(pooledTxsRequestor));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _serializer = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _protocolValidator = protocolValidator ?? throw new ArgumentNullException(nameof(protocolValidator));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _forkInfo = forkInfo ?? throw new ArgumentNullException(nameof(forkInfo));
            _gossipPolicy = gossipPolicy ?? throw new ArgumentNullException(nameof(gossipPolicy));
            _txGossipPolicy = transactionsGossipPolicy ?? ShouldGossip.Instance;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _logger = _logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _protocolFactories = GetProtocolFactories();
            rlpxHost.SessionCreated += SessionCreated;
        }

        private void SessionCreated(object sender, SessionEventArgs e)
        {
            _sessions.TryAdd(e.Session.SessionId, e.Session);
            e.Session.Initialized += SessionInitialized;
            e.Session.Disconnected += SessionDisconnected;
        }

        private void SessionDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (ISession)sender;
            session.Initialized -= SessionInitialized;
            session.Disconnected -= SessionDisconnected;
            _sessions.TryRemove(session.SessionId, out session);
        }

        private void SessionInitialized(object sender, EventArgs e)
        {
            ISession session = (ISession)sender;
            session.RegisterProtocolMessageSpace(new Dictionary<string, int> { { Protocol.P2P, P2PProtocolHandlerFactory.MessageSpace } });
            InitProtocol(session, Protocol.P2P, session.P2PVersion, true);
        }

        private void InitProtocol(ISession session, string protocolCode, int version, bool addCapabilities = false)
        {
            if (session.State < SessionState.Initialized)
            {
                throw new InvalidOperationException($"{nameof(InitProtocol)} called on {session}");
            }

            if (session.State != SessionState.Initialized)
            {
                return;
            }

            IProtocolHandlerFactory? factory = GetProtocolHandlerFactory(protocolCode, version);
            if (factory == null)
            {
                throw new NotSupportedException($"Protocol {protocolCode} {version} is not supported");
            }

            IProtocolHandler protocolHandler = factory.Create(session);
            protocolHandler.SubprotocolRequested += (s, e) =>
            {
                // Select only protocol with factory and order by factory priority so that they are executed in the
                // correct order.
                var protocolAndMessageIdSpace = e.Protocols
                    .Select((p) => (p, GetProtocolHandlerFactory(p.ProtocolCode, p.Version)))
                    .Where((p) => p.Item2 != null)
                    .OrderBy(p => p.Item2.ProtocolPriority)
                    .Select((p => (p.Item1, p.Item2.MessageIdSpaceSize)))
                    .ToList();

                var protocols = protocolAndMessageIdSpace.Select((p) => p.Item1).ToList();
                var messageIdSpaceSize = protocolAndMessageIdSpace.ToDictionary(p => p.Item1.ProtocolCode, p => p.MessageIdSpaceSize);
                session.RegisterProtocolMessageSpace(messageIdSpaceSize);

                for (var i = 0; i < protocols.Count; i++)
                {
                    InitProtocol(session, protocols[i].ProtocolCode, protocols[i].Version);
                }
            };

            session.AddProtocolHandler(protocolHandler);
            if (addCapabilities)
            {
                foreach (Capability capability in _capabilities)
                {
                    session.AddSupportedCapability(capability);
                }
            }

            protocolHandler.Init();
        }

        private IProtocolHandlerFactory? GetProtocolHandlerFactory(string protocolCode, int version)
        {
            protocolCode = protocolCode.ToLowerInvariant();

            if (_protocolFactories.TryGetValue((protocolCode, version), out IProtocolHandlerFactory? handlerFactory))
            {
                return handlerFactory;
            }

            if (_protocolFactories.TryGetValue((protocolCode, AnyVersion), out handlerFactory))
            {
                return handlerFactory;
            }

            return null;
        }

        public void AddProtocol(string code, int version, IProtocolHandlerFactory factory)
        {
            if (_protocolFactories.ContainsKey((code, version)))
            {
                throw new InvalidOperationException($"Protocol {code} was already added.");
            }

            _protocolFactories[(code, version)] = factory;
        }

        private IDictionary<(string, int), IProtocolHandlerFactory> GetProtocolFactories()
            => new Dictionary<(string, int), IProtocolHandlerFactory>
            {
                [(Protocol.P2P, AnyVersion)] = new P2PProtocolHandlerFactory(_rlpxHost, _serializer, _networkConfig, _discoveryApp, _protocolValidator, _stats, _logManager),
                [(Protocol.Eth, 66)] = new Eth66ProtocolHandlerFactory(_protocolValidator, _peerStorage, _syncPool, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _pooledTxsRequestor, _gossipPolicy, _forkInfo, _logManager, _txGossipPolicy),
                [(Protocol.Eth, 67)] = new Eth67ProtocolHandlerFactory(_protocolValidator, _peerStorage, _syncPool, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _pooledTxsRequestor, _gossipPolicy, _forkInfo, _logManager, _txGossipPolicy),
                [(Protocol.Eth, 68)] = new Eth68ProtocolHandlerFactory(_protocolValidator, _peerStorage, _syncPool, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _txPool, _pooledTxsRequestor, _gossipPolicy, _forkInfo, _logManager, _txGossipPolicy),
                [(Protocol.Snap, 1)] = new SnapProtocolHandlerFactory(_protocolValidator, _syncPool, _stats, _serializer, _logManager),
                [(Protocol.NodeData, 1)] = new NodeDataProtocolHandlerFactory(_protocolValidator, _syncPool, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _logManager),
                [(Protocol.Wit, 0)] = new WitProtocolHandlerFactory(_protocolValidator, _syncPool, _stats, _syncServer, _serializer, _logManager),
                [(Protocol.Les, 0)] = new LesProtocolHandlerFactory(_protocolValidator, _peerStorage, _syncPool, _txPool, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _logManager)
            };

        public void AddSupportedCapability(Capability capability)
        {
            _capabilities.Add(capability);
        }

        public void RemoveSupportedCapability(Capability capability)
        {
            if (_capabilities.Remove(capability))
            {
                if (_logger.IsDebug) _logger.Debug($"Removed supported capability: {capability}");
            }
        }

        public void SendNewCapability(Capability capability)
        {
            AddCapabilityMessage message = new(capability);
            foreach ((Guid _, ISession session) in _sessions)
            {
                if (session.HasAgreedCapability(capability))
                {
                    continue;
                }

                if (!session.HasAvailableCapability(capability))
                {
                    continue;
                }

                session.DeliverMessage(message);
            }
        }
    }
}
