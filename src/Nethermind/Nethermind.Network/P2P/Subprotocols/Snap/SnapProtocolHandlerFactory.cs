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

public class SnapProtocolHandlerFactory(
    INodeStatsManager stats,
    IMessageSerializationService serializer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    IWorldStateManager worldStateManager,
    ILogManager logManager) : IProtocolHandlerFactory
{
    private readonly ISnapServer? _snapServer = worldStateManager.SnapServer;

    public string ProtocolCode => Protocol.Snap;

    public bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        if (version != 1)
        {
            handler = null;
            return false;
        }

        handler = new SnapProtocolHandler(
            session, stats, serializer, backgroundTaskScheduler, logManager, _snapServer);
        return true;
    }
}
