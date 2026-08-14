// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// XDC's port of <c>eth/64</c> (EIP-2364): the fork ID handshake plus the XDC-only messages.
/// </summary>
internal class Xdc164ProtocolHandler(
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
    ITxGossipPolicy? transactionsGossipPolicy = null) : Eth64ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy), IStaticProtocolInfo, IXdcConsensusPeer, IXdcMessageContext
{
    private readonly XdcConsensusMessageHandler _consensusMessages =
        new(timeoutCertificateManager, votesManager, syncInfoManager, blockTree, session, logManager);

    public override string Name => "xdc164";

    public static byte Version => XdcProtocolVersions.Xdc164;
    public override byte ProtocolVersion => Version;

    public override int MessageIdSpaceSize => XdcProtocolVersions.LegacyMessageIdSpaceSize;

    protected override bool HandleMessageCore(ZeroPacket message) =>
        _consensusMessages.TryHandle(message, this) || base.HandleMessageCore(message);

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
