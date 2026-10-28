// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Synchronization.Peers;
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
    ISyncPeerPool syncPeerPool,
    ISession session,
    ILogManager logManager)
{
    // A SyncInfo is sent once every few timed-out rounds, so a short window is enough to keep one from circling.
    private const int NotifiedSyncInfoCapacity = 64;

    private readonly AssociativeKeyCache<ValueHash256> _notifiedVotes = new(MemoryAllowance.MemPoolSize / 2);
    private readonly AssociativeKeyCache<ValueHash256> _notifiedTimeouts = new(MemoryAllowance.MemPoolSize / 2);
    private readonly AssociativeKeyCache<ValueHash256> _notifiedSyncInfos = new(NotifiedSyncInfoCapacity);
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

    public bool ShouldNotify(SyncInfo syncInfo)
    {
        if (syncInfo.IsMine)
            return true;

        if (_notifiedSyncInfos.Contains(syncInfo.Hash))
            return false;

        _notifiedSyncInfos.Set(syncInfo.Hash);
        return true;
    }

    private void Handle(SyncInfoMsg syncInfoMsg)
    {
        // The message itself decodes to null from an empty RLP list, just like either certificate does.
        SyncInfo? syncInfo = syncInfoMsg.SyncInfo;
        string? timeoutError = syncInfoManager.ProcessTimeoutCertificate(syncInfo?.HighestTimeoutCert);
        string? quorumError = syncInfoManager.ProcessQuorumCertificate(syncInfo?.HighestQuorumCert);
        LogSkippedCertificate(timeoutError);
        LogSkippedCertificate(quorumError);

        // Propagate only what moved us forward, so a message that taught us nothing stops here.
        if (timeoutError is null || quorumError is null)
            Relay(syncInfo!);
    }

    /// <summary>Forwards a peer's <see cref="SyncInfo"/> to the other peers, as votes and timeouts are forwarded.</summary>
    private void Relay(SyncInfo syncInfo)
    {
        // The sender has it already; marking its session keeps the relay from echoing it straight back.
        _notifiedSyncInfos.Set(syncInfo.Hash);

        foreach (PeerInfo peer in syncPeerPool.AllPeers)
        {
            if (peer.SyncPeer is IXdcConsensusPeer xdcProtocol)
                xdcProtocol.SendSyncInfo(syncInfo);
        }
    }

    private void LogSkippedCertificate(string? error)
    {
        //TODO Disconnect peer?
        if (error is not null && _logger.IsDebug)
            _logger.Debug($"Skipped SyncInfo certificate from peer {session.RemoteNodeId}: {error}");
    }

    /// <summary>Builds the per-session handler, so the protocol handlers take one dependency rather than five.</summary>
    internal sealed class Factory(
        ITimeoutCertificateManager timeoutCertificateManager,
        IVotesManager votesManager,
        ISyncInfoManager syncInfoManager,
        IBlockTree blockTree,
        ISyncPeerPool syncPeerPool,
        ILogManager logManager)
    {
        public XdcConsensusMessageHandler ForSession(ISession session) =>
            new(timeoutCertificateManager, votesManager, syncInfoManager, blockTree, syncPeerPool, session, logManager);
    }
}
