// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Network.P2P.Subprotocols.Snap;

public class SnapProtocolHandlerFactory: SatelliteProtocolHandlerFactory
{
    private readonly INodeStatsManager _stats;
    private readonly IMessageSerializationService _serializer;
    private readonly ILogManager _logManager;

    public SnapProtocolHandlerFactory(
        IProtocolValidator protocolValidator,
        ISyncPeerPool syncPool,
        INodeStatsManager stats,
        IMessageSerializationService serializer,
        ILogManager logManager) : base(protocolValidator, syncPool, logManager)
    {
        _stats = stats;
        _serializer = serializer;
        _logManager = logManager;
    }

    public override int MessageIdSpaceSize => ProtocolMessageIdSpaces.Snap;

    public override ProtocolHandlerBase CreateSatelliteProtocol(ISession session)
    {
        return new SnapProtocolHandler(session, _stats, _serializer, _logManager);
    }
}
