// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using ReceiptsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages.ReceiptsMessage;
using StatusMessage = Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages.StatusMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69;

/// <summary>
/// https://github.com/MariusVanDerWijden/EIPs/blob/eth69_v2/EIPS/eip-7642.md
/// </summary>
public class Eth69ProtocolHandler : Eth68ProtocolHandler
{
    public Eth69ProtocolHandler(ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ITxPool txPool,
        IPooledTxsRequestor pooledTxsRequestor,
        IGossipPolicy gossipPolicy,
        ForkInfo forkInfo,
        ILogManager logManager,
        ITxGossipPolicy? transactionsGossipPolicy = null)
        : base(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, pooledTxsRequestor, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
    {
    }

    public override string Name => "eth69";

    public override byte ProtocolVersion => EthVersions.Eth69;

    public override void HandleMessage(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth62MessageCode.Status:
                StatusMessage statusMsg = Deserialize<StatusMessage>(message.Content);
                base.ReportIn(statusMsg, size);
                Handle(statusMsg);
                break;
            case Eth62MessageCode.NewBlockHashes:
                break;
            case Eth62MessageCode.NewBlock:
                break;
            case Eth63MessageCode.Receipts:
                ReceiptsMessage receiptsMessage = Deserialize<ReceiptsMessage>(message.Content);
                base.ReportIn(receiptsMessage, size);
                base.Handle(receiptsMessage, size);
                break;
            case Eth63MessageCode.GetReceipts:
                GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                ReportIn(getReceiptsMessage, size);
                BackgroundTaskScheduler.ScheduleSyncServe(getReceiptsMessage, Handle);
                break;
            default:
                base.HandleMessage(message);
                break;
        }
    }

    private void Handle(StatusMessage status)
    {
        status.TotalDifficulty = 0; // TODO handle properly for eth/69
        base.Handle(status);
    }

    private new async Task<ReceiptsMessage> Handle(GetReceiptsMessage getReceiptsMessage, CancellationToken cancellationToken)
    {
        V66.Messages.ReceiptsMessage message = await base.Handle(getReceiptsMessage, cancellationToken);
        return new ReceiptsMessage(message.RequestId, message.EthMessage);
    }
}
