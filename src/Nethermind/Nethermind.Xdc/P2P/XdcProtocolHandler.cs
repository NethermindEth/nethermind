// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
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
    ISession session,
    IMessageSerializationService serializer,
    INodeStatsManager nodeStatsManager,
    ISyncServer syncServer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ITxPool txPool,
    IGossipPolicy gossipPolicy,
    IForkInfo forkInfo,
    ILogManager logManager,
    ITxGossipPolicy? transactionsGossipPolicy = null) : Eth65ProtocolHandler(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool, gossipPolicy, forkInfo, logManager, transactionsGossipPolicy)
{
    private readonly ITimeoutCertificateManager _timeoutCertificateManager = timeoutCertificateManager;
    private readonly IVotesManager _votesManager = votesManager;
    private ClockKeyCache<ValueHash256> _notifiedVotes = new(MemoryAllowance.MemPoolSize / 2);
    private ClockKeyCache<ValueHash256> _notifiedTimeouts = new(MemoryAllowance.MemPoolSize / 2);

    public override string Name => "xdpos2";

    public override byte ProtocolVersion => 100;

    public override string ProtocolCode => "eth";

    public override int MessageIdSpaceSize => XdcMessageCode.SyncInfoMsg + 1;

    protected override TimeSpan InitTimeout => base.InitTimeout;

    public override void HandleMessage(ZeroPacket message)
    {
        var size = message.Content.ReadableBytes;

        int packetType = message.PacketType;

        switch (packetType)
        {
            case XdcMessageCode.VoteMsg:
                {
                    using VoteMsg voteMsg = Deserialize<VoteMsg>(message.Content);
                    ReportIn(voteMsg, size);
                    Handle(voteMsg);
                    break;
                }
            case XdcMessageCode.TimeoutMsg:
                {
                    using TimeoutMsg timeoutMsg = Deserialize<TimeoutMsg>(message.Content);
                    ReportIn(timeoutMsg, size);
                    Handle(timeoutMsg);
                    break;
                }
            case XdcMessageCode.SyncInfoMsg:
                {
                    using SyncInfoMsg syncInfoMsg = Deserialize<SyncInfoMsg>(message.Content);
                    ReportIn(syncInfoMsg, size);
                    Handle(syncInfoMsg);
                    break;
                }
            default:
                {
                    base.HandleMessage(message);
                    break;
                }
        }
    }

    protected override void EnrichStatusMessage(StatusMessage statusMessage)
    {
        // We do not want to add ForkId to status message in XDPoS
    }

    private void Handle(VoteMsg voteMsg)
    {
        _votesManager.OnReceiveVote(voteMsg.Vote);
    }
    private void Handle(TimeoutMsg timeoutMsg)
    {
        _timeoutCertificateManager.OnReceiveTimeout(timeoutMsg.Timeout);
    }
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

    public void SendSyncInfo(SyncInfo syncInfo)
    {
        Send(new SyncInfoMsg() { SyncInfo = syncInfo });
    }

    private bool ShouldNotifyVote(Vote vote)
    {
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
