// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Network.P2P.Subprotocols.NodeData;

public class NodeDataProtocolHandlerFactory: SatelliteProtocolHandlerFactory
{
    private readonly IMessageSerializationService _serializer;
    private readonly INodeStatsManager _stats;
    private readonly ISyncServer _syncServer;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly ILogManager _logManager;

    public override int MessageIdSpaceSize => 2;

    public NodeDataProtocolHandlerFactory(
        IProtocolValidator protocolValidator,
        ISyncPeerPool syncPool,
        IMessageSerializationService serializer,
        INodeStatsManager stats,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager
    ) : base(protocolValidator, syncPool, logManager)
    {
        _serializer = serializer;
        _stats = stats;
        _syncServer = syncServer;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _logManager = logManager;
    }

    public override ProtocolHandlerBase CreateSatelliteProtocol(ISession session)
    {
        return new NodeDataProtocolHandler(session, _serializer, _stats, _syncServer, _backgroundTaskScheduler,
            _logManager);
    }
}
