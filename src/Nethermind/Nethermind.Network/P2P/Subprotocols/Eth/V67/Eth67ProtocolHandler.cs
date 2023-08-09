// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V67;

/// <summary>
/// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-4938.md
/// </summary>
public class Eth67ProtocolHandler : Eth66ProtocolHandler
{
    public Eth67ProtocolHandler(ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        ITxPool txPool,
        IPooledTxsRequestor pooledTxsRequestor,
        IGossipPolicy gossipPolicy,
        ForkInfo forkInfo,
        ILogManager logManager,
        ITxGossipPolicy? transactionsGossipPolicy = null)
        : base(session, serializer, nodeStatsManager, syncServer, txPool, pooledTxsRequestor, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
    {
    }

    public override string Name => "eth67";

    public override byte ProtocolVersion => EthVersions.Eth67;

    public override void HandleMessage(ZeroPacket message)
    {
        switch (message.PacketType)
        {
            case Eth66MessageCode.GetNodeData:
                break;
            case Eth66MessageCode.NodeData:
                break;
            default:
                base.HandleMessage(message);
                break;
        }
    }
}
