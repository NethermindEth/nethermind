// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

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
            case Eth69MessageCode.NewBlockHashes:
                break;
            case Eth69MessageCode.NewBlock:
                break;
            case Eth69MessageCode.Status:
                StatusMessage69 statusMsg = Deserialize<StatusMessage69>(message.Content);
                base.ReportIn(statusMsg, size);
                this.Handle(statusMsg);
                break;
            case Eth69MessageCode.Receipts:
                ReceiptsMessage69 receiptsMessage = Deserialize<ReceiptsMessage69>(message.Content);
                base.ReportIn(receiptsMessage, size);
                base.Handle(receiptsMessage, size);
                break;
            case Eth69MessageCode.GetReceipts:
                GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                ReportIn(getReceiptsMessage, size);
                BackgroundTaskScheduler.ScheduleSyncServe(getReceiptsMessage, this.Handle);
                break;
            default:
                base.HandleMessage(message);
                break;
        }
    }

    private void Handle(StatusMessage69 status)
    {
        status.TotalDifficulty = 0; // TODO handle properly for eth/69
        base.Handle(status);
    }

    private new async Task<ReceiptsMessage69> Handle(GetReceiptsMessage getReceiptsMessage, CancellationToken cancellationToken)
    {
        ReceiptsMessage message = await base.Handle(getReceiptsMessage, cancellationToken);
        return new(message.RequestId, message.EthMessage);
    }

    protected override void NotifyOfStatus(BlockHeader head)
    {
        StatusMessage69 statusMessage = new()
        {
            NetworkId = SyncServer.NetworkId,
            ProtocolVersion = ProtocolVersion,
            BestHash = head.Hash!,
            GenesisHash = SyncServer.Genesis.Hash!
        };

        EnrichStatusMessage(statusMessage);

        Send(statusMessage);
    }

    public override void NotifyOfNewBlock(Block block, SendBlockMode mode)
    {
        // Skip
    }
}
