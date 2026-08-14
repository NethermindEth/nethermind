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
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// XDPoS 2.0 legacy protocol: <c>eth/63</c> semantics plus the XDC-only messages, with no fork ID in the handshake.
/// </summary>
internal class XdcProtocolHandler(
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
    ILogManager logManager,
    ITxGossipPolicy? transactionsGossipPolicy = null) : Eth63ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, logManager, transactionsGossipPolicy), IStaticProtocolInfo, IXdcConsensusPeer, IXdcMessageContext
{
    private readonly XdcConsensusMessageHandler _consensusMessages =
        new(timeoutCertificateManager, votesManager, syncInfoManager, blockTree, session, logManager);

    public override string Name => "xdpos2";

    public static byte Version => XdcProtocolVersions.Legacy;
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
