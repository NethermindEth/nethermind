// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
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
using System;

namespace Nethermind.Xdc.P2P;

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
    ITxGossipPolicy? transactionsGossipPolicy = null) : Eth63ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, logManager, transactionsGossipPolicy), IStaticProtocolInfo
{
    private AssociativeKeyCache<ValueHash256> _notifiedVotes = new(MemoryAllowance.MemPoolSize / 2);
    private AssociativeKeyCache<ValueHash256> _notifiedTimeouts = new(MemoryAllowance.MemPoolSize / 2);

    public override string Name => "xdpos2";

    public static byte Version => 100;
    public override byte ProtocolVersion => Version;

    public override int MessageIdSpaceSize => XdcMessageCode.SyncInfoMsg + 1;

    protected override TimeSpan InitTimeout => base.InitTimeout;

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;

        int packetType = message.PacketType;

        (bool isSyncing, _, _) = blockTree.IsSyncing(XdcConstants.MaxSyncDistanceForConsensus);
        if (isSyncing && XdcMessageCode.IsXdcMessage(packetType))
        {
            const string ignored = $"XDC message ignored, syncing";
            ReportIn(ignored, size);
            return true;
        }

        switch (packetType)
        {
            case XdcMessageCode.VoteMsg:
                {
                    using VoteMsg voteMsg = Deserialize<VoteMsg>(message.Content);
                    ReportIn(voteMsg, size);
                    Handle(voteMsg);
                    return true;
                }
            case XdcMessageCode.TimeoutMsg:
                {
                    using TimeoutMsg timeoutMsg = Deserialize<TimeoutMsg>(message.Content);
                    ReportIn(timeoutMsg, size);
                    Handle(timeoutMsg);
                    return true;
                }
            case XdcMessageCode.SyncInfoMsg:
                {
                    using SyncInfoMsg syncInfoMsg = Deserialize<SyncInfoMsg>(message.Content);
                    ReportIn(syncInfoMsg, size);
                    Handle(syncInfoMsg);
                    return true;
                }
            default:
                return base.HandleMessageCore(message);
        }
    }

    private void Handle(VoteMsg voteMsg) => _ = votesManager.OnReceiveVote(voteMsg.Vote);
    private void Handle(TimeoutMsg timeoutMsg) => _ = timeoutCertificateManager.OnReceiveTimeout(timeoutMsg.Timeout);
    private void Handle(SyncInfoMsg syncInfoMsg)
    {
        if (!syncInfoManager.VerifySyncInfo(syncInfoMsg.SyncInfo, out string error))
        {
            //TODO Disconnect peer?
            if (Logger.IsDebug) Logger.Debug($"Received useless SyncInfo from peer {Session.RemoteNodeId}: {error}");
            return;
        }
        syncInfoManager.ProcessSyncInfo(syncInfoMsg.SyncInfo);
    }

    public void SendVote(Vote vote)
    {
        if (!ShouldNotifyVote(vote))
            return;
        Send(new VoteMsg() { Vote = vote });
    }

    public void SendTimeout(Timeout timeout)
    {
        if (!ShouldNotifyTimeout(timeout))
            return;
        Send(new TimeoutMsg() { Timeout = timeout });
    }

    public void SendSyncInfo(SyncInfo syncInfo) => Send(new SyncInfoMsg() { SyncInfo = syncInfo });

    private bool ShouldNotifyVote(Vote vote)
    {
        if (vote.IsMyVote)
            return true;

        if (_notifiedVotes.Contains(vote.Hash))
            return false;

        _notifiedVotes.Set(vote.Hash);
        return true;
    }

    private bool ShouldNotifyTimeout(Timeout timeout)
    {
        if (timeout.IsMyVote)
            return true;

        if (_notifiedTimeouts.Contains(timeout.Hash))
            return false;

        _notifiedTimeouts.Set(timeout.Hash);
        return true;
    }
}
