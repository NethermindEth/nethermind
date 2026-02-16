// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P.Eth100;

namespace Nethermind.Xdc.P2P
{
    /// <summary>
    /// Factory for creating Eth100ProtocolHandler instances.
    /// Registered with ProtocolsManager to handle eth/100 protocol version.
    /// </summary>
    public class Eth100ProtocolFactory : ICustomEthProtocolFactory
    {
        private readonly IMessageSerializationService _serializer;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ISyncServer _syncServer;
        private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
        private readonly ITxPool _txPool;
        private readonly IGossipPolicy _gossipPolicy;
        private readonly ILogManager _logManager;
        private readonly IXdcConsensusMessageProcessor? _consensusProcessor;
        private readonly ITxGossipPolicy? _txGossipPolicy;

        public Eth100ProtocolFactory(
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ITxPool txPool,
            IGossipPolicy gossipPolicy,
            ILogManager logManager,
            ITxGossipPolicy? txGossipPolicy,
            IXdcConsensusMessageProcessor? consensusProcessor)
        {
            _serializer = serializer;
            _nodeStatsManager = nodeStatsManager;
            _syncServer = syncServer;
            _backgroundTaskScheduler = backgroundTaskScheduler;
            _txPool = txPool;
            _gossipPolicy = gossipPolicy;
            _logManager = logManager;
            _txGossipPolicy = txGossipPolicy;
            _consensusProcessor = consensusProcessor;
        }

        public bool CanHandle(int version) => version == 100;

        public SyncPeerProtocolHandlerBase? Create(ISession session, int version)
        {
            if (version != 100)
                return null;

            return new Eth100ProtocolHandler(
                session,
                _serializer,
                _nodeStatsManager,
                _syncServer,
                _backgroundTaskScheduler,
                _txPool,
                _gossipPolicy,
                _logManager,
                _consensusProcessor,
                _txGossipPolicy);
        }
    }
}
