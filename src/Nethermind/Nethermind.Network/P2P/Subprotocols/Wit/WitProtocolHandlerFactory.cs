// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Network.P2P.Subprotocols.Wit;

public class WitProtocolHandlerFactory: SatelliteProtocolHandlerFactory
{
    private readonly INodeStatsManager _stats;
    private readonly IMessageSerializationService _serializer;
    private readonly ISyncServer _syncServer;
    private readonly ILogManager _logManager;

    public WitProtocolHandlerFactory(
        IProtocolValidator protocolValidator,
        ISyncPeerPool syncPool,
        INodeStatsManager stats,
        ISyncServer syncServer,
        IMessageSerializationService serializer,
        ILogManager logManager) : base(protocolValidator, syncPool, logManager)
    {
        _stats = stats;
        _serializer = serializer;
        _syncServer = syncServer;
        _logManager = logManager;
    }

    public override int MessageIdSpaceSize => ProtocolMessageIdSpaces.Wit;

    public override ProtocolHandlerBase CreateSatelliteProtocol(ISession session)
    {
        return new WitProtocolHandler(session, _serializer, _stats, _syncServer, _logManager);
    }
}
