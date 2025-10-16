// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-7642
/// </summary>
public class Eth69ProtocolHandler(
    ISession session,
    IMessageSerializationService serializer,
    INodeStatsManager nodeStatsManager,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IPooledTxsRequestor pooledTxsRequestor,
    IGossipPolicy gossipPolicy,
    IForkInfo forkInfo,
    ILogManager logManager,
    ITxGossipPolicy? transactionsGossipPolicy = null)
    : Eth68ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool,
        pooledTxsRequestor, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy), ISyncPeer
{
    public override string Name => "eth69";

    public override byte ProtocolVersion => EthVersions.Eth69;

    public override int MessageIdSpaceSize => 18;

    // Explicitly mark as not supported
    public override UInt256? TotalDifficulty
    {
        get => null;
        set { }
    }

    public override event EventHandler<ProtocolInitializedEventArgs>? ProtocolInitialized;

    public override void HandleMessage(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth69MessageCode.Status:
                StatusMessage69 statusMsg = Deserialize<StatusMessage69>(message.Content);
                ReportIn(statusMsg, size);
                Handle(statusMsg);
                break;
            case Eth69MessageCode.Receipts:
                ReceiptsMessage69 receiptsMessage = Deserialize<ReceiptsMessage69>(message.Content);
                ReportIn(receiptsMessage, size);
                base.Handle(receiptsMessage, size);
                break;
            case Eth69MessageCode.GetReceipts:
                HandleInBackground<GetReceiptsMessage, ReceiptsMessage69>(message, Handle);
                break;
            case Eth69MessageCode.BlockRangeUpdate:
                BlockRangeUpdateMessage blockRangeUpdateMsg = Deserialize<BlockRangeUpdateMessage>(message.Content);
                ReportIn(blockRangeUpdateMsg, size);
                Handle(blockRangeUpdateMsg);
                break;
            default:
                base.HandleMessage(message);
                break;
        }
    }

    private void Handle(StatusMessage69 status)
    {
        if (_statusReceived)
        {
            throw new SubprotocolException("StatusMessage has already been received in the past");
        }

        _statusReceived = true;
        _remoteHeadBlockHash = status.LatestBlockHash;

        ReceivedProtocolInitMsg(status);

        SyncPeerProtocolInitializedEventArgs eventArgs = new(this)
        {
            NetworkId = (ulong)status.NetworkId,
            BestHash = status.LatestBlockHash,
            GenesisHash = status.GenesisHash,
            Protocol = status.Protocol,
            ProtocolVersion = status.ProtocolVersion,
            ForkId = status.ForkId
        };

        Session.IsNetworkIdMatched = SyncServer.NetworkId == (ulong)status.NetworkId;
        HeadNumber = status.LatestBlock;
        HeadHash = status.LatestBlockHash;
        ProtocolInitialized?.Invoke(this, eventArgs);
    }

    private void Handle(BlockRangeUpdateMessage blockRangeUpdate)
    {
        if (blockRangeUpdate.EarliestBlock > blockRangeUpdate.LatestBlock)
        {
            Disconnect(
                DisconnectReason.InvalidBlockRangeUpdate,
                $"BlockRangeUpdate with earliest ({blockRangeUpdate.EarliestBlock}) > latest ({blockRangeUpdate.LatestBlock})."
            );
        }

        if (blockRangeUpdate.LatestBlockHash.IsZero)
        {
            Disconnect(
                DisconnectReason.InvalidBlockRangeUpdate,
                "BlockRangeUpdate with latest block hash as zero."
            );
        }

        _remoteHeadBlockHash = blockRangeUpdate.LatestBlockHash;
        HeadNumber = blockRangeUpdate.LatestBlock;
        HeadHash = blockRangeUpdate.LatestBlockHash;
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
            ProtocolVersion = ProtocolVersion,
            NetworkId = SyncServer.NetworkId,
            GenesisHash = SyncServer.Genesis.Hash!,
            ForkId = _forkInfo.GetForkId(head.Number, head.Timestamp),
            EarliestBlock = SyncServer.LowestBlock,
            LatestBlock = head.Number,
            LatestBlockHash = head.Hash!
        };

        Send(statusMessage);
    }

    public void NotifyOfNewRange(BlockHeader earliest, BlockHeader latest)
    {
        if (earliest.Number > latest.Number)
            throw new ArgumentException($"Earliest block ({earliest.Number}) greater than latest ({latest.Number}) in BlockRangeUpdate.");

        if (latest.Hash is null || latest.Hash.IsZero)
            throw new ArgumentException($"Latest block ({latest.Number}) hash is not provided.");

        if (Logger.IsTrace)
            Logger.Trace($"OUT {Counter:D5} BlockRangeUpdate to {Node:c}");

        BlockRangeUpdateMessage msg = new()
        {
            EarliestBlock = earliest.Number,
            LatestBlock = latest.Number,
            LatestBlockHash = latest.Hash
        };

        Send(msg);
    }
}
