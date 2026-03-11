// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.Subprotocols.NodeData;

public class NodeDataProtocolHandlerFactory : IProtocolHandlerFactory
{
    private readonly IMessageSerializationService _serializer;
    private readonly INodeStatsManager _stats;
    private readonly ISyncServer _syncServer;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly ILogManager _logManager;

    public NodeDataProtocolHandlerFactory(
        IMessageSerializationService serializer,
        INodeStatsManager stats,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager)
    {
        _serializer = serializer;
        _stats = stats;
        _syncServer = syncServer;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _logManager = logManager;
    }

    public string ProtocolCode => Protocol.NodeData;

    public bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        if (version != 1)
        {
            handler = null;
            return false;
        }

        handler = new NodeDataProtocolHandler(
            session, _serializer, _stats, _syncServer, _backgroundTaskScheduler, _logManager);
        return true;
    }
}
