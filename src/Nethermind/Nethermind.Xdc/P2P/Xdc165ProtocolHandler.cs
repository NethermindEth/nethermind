// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P.Messages;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// XDC's port of <c>eth/65</c> (EIP-2464): transaction announcements on the relocated codes
/// <c>0xe3</c>-<c>0xe5</c>, because <c>0x08</c>/<c>0x09</c> already carry order and lending transactions.
/// </summary>
internal class Xdc165ProtocolHandler(
    ITimeoutCertificateManager timeoutCertificateManager,
    IVotesManager votesManager,
    ISyncInfoManager syncInfoManager,
    IBlockTree blockTree,
    ISession session,
    IMessageSerializationService serializer,
    INodeStatsManager nodeStatsManager,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IGossipPolicy gossipPolicy,
    IForkInfo forkInfo,
    ILogManager logManager,
    ITxGossipPolicy? transactionsGossipPolicy = null) : Eth65ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy), IStaticProtocolInfo, IXdcConsensusPeer, IXdcMessageContext
{
    private readonly XdcConsensusMessageHandler _consensusMessages =
        new(timeoutCertificateManager, votesManager, syncInfoManager, blockTree, session, logManager);

    public override string Name => "xdc165";

    public static byte Version => XdcProtocolVersions.Xdc165;
    public override byte ProtocolVersion => Version;

    public override int MessageIdSpaceSize => XdcProtocolVersions.MessageIdSpaceSize;

    /// <remarks>
    /// The three EIP-2464 payloads are byte-identical to upstream's, so an inbound message is translated to the
    /// upstream code and handled by the base class. Only the outbound codes differ, and those come from the
    /// message types themselves.
    /// </remarks>
    protected override bool HandleMessageCore(ZeroPacket message)
    {
        if (_consensusMessages.TryHandle(message, this))
            return true;

        switch (message.PacketType)
        {
            case XdcMessageCode.NewPooledTransactionHashes:
                message.PacketType = Eth65MessageCode.NewPooledTransactionHashes;
                break;
            case XdcMessageCode.GetPooledTransactions:
                message.PacketType = Eth65MessageCode.GetPooledTransactions;
                break;
            case XdcMessageCode.PooledTransactions:
                message.PacketType = Eth65MessageCode.PooledTransactions;
                break;
            case Eth65MessageCode.PooledTransactions:
                // 0x0a is unassigned in XDC; do not let the base class read it as a response.
                return false;
        }

        return base.HandleMessageCore(message);
    }

    protected override void Handle(NewPooledTransactionHashesMessage msg) =>
        RequestPooledTransactions<XdcGetPooledTransactionsMessage>(msg.Hashes);

    protected override PooledTransactionsMessage CreatePooledTransactionsMessage(IOwnedReadOnlyList<Transaction> transactions) =>
        new XdcPooledTransactionsMessage(transactions);

    protected override NewPooledTransactionHashesMessage CreateAnnouncementMessage(IOwnedReadOnlyList<Hash256> hashes) =>
        new XdcNewPooledTransactionHashesMessage(hashes);

    public override void HandleMessage(PooledTransactionRequestMessage message)
    {
        using ArrayPoolList<Hash256> hashesToRetry = new(1) { new Hash256(message.TxHash) };
        RequestPooledTransactions<XdcGetPooledTransactionsMessage>(hashesToRetry, registerForRetry: false);
    }

    public override void HandleMessages(ReadOnlySpan<ValueHash256> txHashes) =>
        HandleMessages<XdcGetPooledTransactionsMessage>(txHashes);

    public void SendVote(Vote vote)
    {
        if (_consensusMessages.ShouldNotify(vote))
            Send(new VoteMsg() { Vote = vote });
    }

    public void SendTimeout(Timeout timeout)
    {
        if (_consensusMessages.ShouldNotify(timeout))
            Send(new TimeoutMsg() { Timeout = timeout });
    }

    public void SendSyncInfo(SyncInfo syncInfo) => Send(new SyncInfoMsg() { SyncInfo = syncInfo });

    T IXdcMessageContext.Decode<T>(IByteBuffer buffer) => Deserialize<T>(buffer);

    void IXdcMessageContext.Report(MessageBase message, int size) => ReportIn(message, size);

    void IXdcMessageContext.Report(string messageInfo, int size) => ReportIn(messageInfo, size);
}
