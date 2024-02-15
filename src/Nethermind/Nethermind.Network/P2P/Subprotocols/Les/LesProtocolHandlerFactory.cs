// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Les;

public class LesProtocolHandlerFactory: SyncProtocolHandlerFactory
{
    private readonly IMessageSerializationService _serializer;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly ISyncServer _syncServer;
    private readonly ILogManager _logManager;

    public LesProtocolHandlerFactory(
        IProtocolValidator protocolValidator,
        INetworkStorage peerStorage,
        ISyncPeerPool syncPool,
        ITxPool txPool,
        IMessageSerializationService serializer,
        INodeStatsManager stats,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager)
        : base(protocolValidator, peerStorage, syncPool, txPool, stats, logManager)
    {
        _serializer = serializer;
        _syncServer = syncServer;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _logManager = logManager;
    }

    public override int MessageIdSpaceSize => ProtocolMessageIdSpaces.Les;

    protected override SyncPeerProtocolHandlerBase CreateSyncProtocolHandler(ISession session)
    {
        return new LesProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _logManager);
    }
}
