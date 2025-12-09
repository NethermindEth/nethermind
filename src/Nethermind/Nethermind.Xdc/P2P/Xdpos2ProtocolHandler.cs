// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using System;

namespace Nethermind.Xdc.P2P;
internal class Xdpos2ProtocolHandler(
    ITimeoutCertificateManager timeoutCertificateManager,
    IVotesManager votesManager,
    ISyncInfoManager syncInfoManager,
    ISession session,
    IMessageSerializationService serializer,
    INodeStatsManager statsManager,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ILogManager logManager) : ZeroProtocolHandlerBase(session, statsManager, serializer, backgroundTaskScheduler, logManager), IZeroProtocolHandler
{
    private readonly ITimeoutCertificateManager _timeoutCertificateManager = timeoutCertificateManager;
    private readonly IVotesManager _votesManager = votesManager;

    public override string Name => "xdpos2";

    public override byte ProtocolVersion => 100;

    public override string ProtocolCode => "eth";

    public override int MessageIdSpaceSize => 18;

    protected override TimeSpan InitTimeout => throw new NotImplementedException();

    public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
    public override event EventHandler<ProtocolEventArgs> SubprotocolRequested;

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
    {
    }

    public override void Dispose()
    {        
    }

    public override void HandleMessage(ZeroPacket message)
    {
        var size = message.Content.ReadableBytes;

        int packetType = message.PacketType;

        switch (packetType)
        {
            case Xdpos2MessageCode.VoteMsg:
                {
                    using VoteMsg voteMsg = Deserialize<VoteMsg>(message.Content);
                    ReportIn(voteMsg, size);
                    Handle(voteMsg);
                    break;
                }
            case Xdpos2MessageCode.TimeoutMsg:
                {
                    using TimeoutMsg timeoutMsg = Deserialize<TimeoutMsg>(message.Content);
                    ReportIn(timeoutMsg, size);
                    Handle(timeoutMsg);
                    break;
                }
            case Xdpos2MessageCode.SyncInfoMsg:
                {
                    using SyncInfoMsg syncInfoMsg = Deserialize<SyncInfoMsg>(message.Content);
                    ReportIn(syncInfoMsg, size);
                    Handle(syncInfoMsg);
                    break;
                }
        }
    }

    public override void Init()
    {
        var x = SubprotocolRequested;
        ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
    }

    private void Handle(VoteMsg voteMsg)
    {
        _votesManager.HandleVote(voteMsg.Vote);
    }
    private void Handle(TimeoutMsg timeoutMsg)
    {
        _timeoutCertificateManager.HandleTimeoutVote(timeoutMsg.Timeout);
    }
    private void Handle(SyncInfoMsg syncInfoMsg)
    {
        if (!syncInfoManager.VerifySyncInfo(syncInfoMsg.SyncInfo, out string error))
        {
            //TODO Disconnect peer?
            if(Logger.IsDebug) Logger.Debug($"Received invalid SyncInfo from peer {Session.RemoteNodeId}: {error}");
            return;
        }
        syncInfoManager.ProcessSyncInfo(syncInfoMsg.SyncInfo);
    }
}
