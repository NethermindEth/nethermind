// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.TxPool;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// Handles the messages that every XDC protocol version carries on top of its <c>ethNN</c> base: the XDPoS 2.0
/// consensus messages (<c>0xe0</c>-<c>0xe2</c>) and the XDC-only order and lending broadcasts (<c>0x08</c>/<c>0x09</c>).
/// </summary>
/// <remarks>
/// One instance per session: the notification caches track what has already been sent to a single peer.
/// </remarks>
internal sealed class XdcConsensusMessageHandler(
    ITimeoutCertificateManager timeoutCertificateManager,
    IVotesManager votesManager,
    ISyncInfoManager syncInfoManager,
    IBlockTree blockTree,
    ISession session,
    ILogManager logManager)
{
    private readonly AssociativeKeyCache<ValueHash256> _notifiedVotes = new(MemoryAllowance.MemPoolSize / 2);
    private readonly AssociativeKeyCache<ValueHash256> _notifiedTimeouts = new(MemoryAllowance.MemPoolSize / 2);
    private readonly ILogger _logger = logManager.GetClassLogger<XdcConsensusMessageHandler>();

    /// <summary>Handles <paramref name="message"/> if it is an XDC-specific message.</summary>
    /// <returns><c>true</c> when the message was handled or deliberately ignored; <c>false</c> to let the base handler decide.</returns>
    public bool TryHandle(ZeroPacket message, IXdcMessageContext context)
    {
        int size = message.Content.ReadableBytes;
        int packetType = message.PacketType;

        if (packetType is XdcMessageCode.OrderTx or XdcMessageCode.LendingTx)
        {
            // TomoX order and lending transactions are broadcast unsolicited on every protocol version.
            // A client that does not follow the orderbook still has to tolerate them to stay peered.
            const string ignored = "Order/lending transaction ignored";
            context.Report(ignored, size);
            return true;
        }

        if (packetType is XdcMessageCode.VoteMsg or XdcMessageCode.TimeoutMsg)
        {
            (bool isSyncing, ulong headNumber, ulong bestSuggested) = blockTree.IsSyncing(XdcConstants.MaxSyncDistanceForConsensus);
            bool isGenesisBootstrap = headNumber == 0 && bestSuggested == 0;
            if (isSyncing && !isGenesisBootstrap)
            {
                const string ignored = "XDC message ignored, syncing";
                context.Report(ignored, size);
                return true;
            }
        }

        switch (packetType)
        {
            case XdcMessageCode.VoteMsg:
                {
                    using VoteMsg voteMsg = context.Decode<VoteMsg>(message.Content);
                    context.Report(voteMsg, size);
                    _ = votesManager.OnReceiveVote(voteMsg.Vote);
                    return true;
                }
            case XdcMessageCode.TimeoutMsg:
                {
                    using TimeoutMsg timeoutMsg = context.Decode<TimeoutMsg>(message.Content);
                    context.Report(timeoutMsg, size);
                    _ = timeoutCertificateManager.OnReceiveTimeout(timeoutMsg.Timeout);
                    return true;
                }
            case XdcMessageCode.SyncInfoMsg:
                {
                    using SyncInfoMsg syncInfoMsg = context.Decode<SyncInfoMsg>(message.Content);
                    context.Report(syncInfoMsg, size);
                    Handle(syncInfoMsg);
                    return true;
                }
            default:
                return false;
        }
    }

    public bool ShouldNotify(Vote vote)
    {
        if (vote.IsMyVote)
            return true;

        if (_notifiedVotes.Contains(vote.Hash))
            return false;

        _notifiedVotes.Set(vote.Hash);
        return true;
    }

    public bool ShouldNotify(Timeout timeout)
    {
        if (timeout.IsMyVote)
            return true;

        if (_notifiedTimeouts.Contains(timeout.Hash))
            return false;

        _notifiedTimeouts.Set(timeout.Hash);
        return true;
    }

    private void Handle(SyncInfoMsg syncInfoMsg)
    {
        if (!syncInfoManager.VerifySyncInfo(syncInfoMsg.SyncInfo, out string error))
        {
            //TODO Disconnect peer?
            if (_logger.IsDebug) _logger.Debug($"Received useless SyncInfo from peer {session.RemoteNodeId}: {error}");
            return;
        }
        syncInfoManager.ProcessSyncInfo(syncInfoMsg.SyncInfo);
    }

    /// <summary>Builds the per-session handler, so the protocol handlers take one dependency rather than five.</summary>
    internal sealed class Factory(
        ITimeoutCertificateManager timeoutCertificateManager,
        IVotesManager votesManager,
        ISyncInfoManager syncInfoManager,
        IBlockTree blockTree,
        ILogManager logManager)
    {
        public XdcConsensusMessageHandler ForSession(ISession session) =>
            new(timeoutCertificateManager, votesManager, syncInfoManager, blockTree, session, logManager);
    }
}
