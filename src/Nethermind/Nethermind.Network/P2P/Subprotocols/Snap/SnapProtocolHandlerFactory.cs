// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Stats;

namespace Nethermind.Network.P2P.Subprotocols.Snap;

public class SnapProtocolHandlerFactory : IProtocolHandlerFactory
{
    private readonly INodeStatsManager _stats;
    private readonly IMessageSerializationService _serializer;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly ILogManager _logManager;
    private readonly ISnapServer? _snapServer;

    public SnapProtocolHandlerFactory(
        INodeStatsManager stats,
        IMessageSerializationService serializer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        IWorldStateManager worldStateManager,
        ILogManager logManager)
    {
        _stats = stats;
        _serializer = serializer;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _logManager = logManager;
        _snapServer = worldStateManager.SnapServer;
    }

    public string ProtocolCode => Protocol.Snap;

    public bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        if (version != 1)
        {
            handler = null;
            return false;
        }

        handler = new SnapProtocolHandler(
            session, _stats, _serializer, _backgroundTaskScheduler, _logManager, _snapServer);
        return true;
    }
}
