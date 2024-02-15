// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats;

namespace Nethermind.AccountAbstraction.Network;

public class AaProtocolHandlerFactory: IProtocolHandlerFactory
{
    private readonly IMessageSerializationService _serializer;
    private readonly INodeStatsManager _nodeStatsManager;
    private readonly IDictionary<Address, IUserOperationPool> _userOperationPools;
    private readonly IAccountAbstractionPeerManager _peerManager;
    private readonly ILogManager _logManager;
    public int ProtocolPriority => ProtocolPriorities.Satellite;
    public int MessageIdSpaceSize => ProtocolMessageIdSpaces.Wit;

    public AaProtocolHandlerFactory(
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        IDictionary<Address, IUserOperationPool> userOperationPools,
        IAccountAbstractionPeerManager peerManager,
        ILogManager logManager)
    {
        _serializer = serializer;
        _nodeStatsManager = nodeStatsManager;
        _userOperationPools = userOperationPools;
        _peerManager = peerManager;
        _logManager = logManager;
    }

    public IProtocolHandler Create(ISession session)
    {
        return new AaProtocolHandler(session, _serializer, _nodeStatsManager, _userOperationPools, _peerManager, _logManager);
    }
}
