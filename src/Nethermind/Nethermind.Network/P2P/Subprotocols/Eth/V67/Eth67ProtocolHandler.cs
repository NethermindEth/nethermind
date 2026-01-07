// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V67;

/// <summary>
/// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-4938.md
/// </summary>
public class Eth67ProtocolHandler(
    ISession session,
    IMessageSerializationService serializer,
    INodeStatsManager nodeStatsManager,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IGossipPolicy gossipPolicy,
    IForkInfo forkInfo,
    ILogManager logManager,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth66ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool,
        gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
{
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
