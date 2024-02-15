// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V67;

public class Eth67ProtocolHandlerFactory: SyncProtocolHandlerFactory
{
    private readonly IMessageSerializationService _serializer;
    private readonly ISyncServer _syncServer;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly IPooledTxsRequestor _pooledTxsRequestor;
    private readonly IGossipPolicy _gossipPolicy;
    private readonly ForkInfo _forkInfo;
    private readonly ILogManager _logManager;
    private readonly ITxGossipPolicy? _txGossipPolicy;

    public override int MessageIdSpaceSize => ProtocolMessageIdSpaces.Eth67;

    public Eth67ProtocolHandlerFactory(
        IProtocolValidator protocolValidator,
        INetworkStorage peerStorage,
        ISyncPeerPool syncPool,
        IMessageSerializationService serializer,
        INodeStatsManager stats,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ITxPool txPool,
        IPooledTxsRequestor pooledTxsRequestor,
        IGossipPolicy gossipPolicy,
        ForkInfo forkInfo,
        ILogManager logManager,
        ITxGossipPolicy txGossipPolicy
    ) :
        base(
            protocolValidator,
            peerStorage,
            syncPool,
            txPool,
            stats,
            logManager)
    {
        _serializer = serializer;
        _syncServer = syncServer;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _pooledTxsRequestor = pooledTxsRequestor;
        _gossipPolicy = gossipPolicy;
        _forkInfo = forkInfo;
        _logManager = logManager;
        _txGossipPolicy = txGossipPolicy;
    }

    protected override SyncPeerProtocolHandlerBase CreateSyncProtocolHandler(ISession session)
    {
        return new Eth67ProtocolHandler(
            session,
            _serializer,
            _stats,
            _syncServer,
            _backgroundTaskScheduler,
            _txPool,
            _pooledTxsRequestor,
            _gossipPolicy,
            _forkInfo,
            _logManager,
            _txGossipPolicy);
    }

}
