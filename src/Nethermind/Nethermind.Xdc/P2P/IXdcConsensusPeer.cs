// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

/// <summary>
/// A peer that can carry XDPoS 2.0 consensus messages, implemented by every XDC protocol version.
/// </summary>
/// <remarks>
/// The sending policy lives here rather than in the handlers: each XDC version derives from a different
/// <c>ethNN</c> handler, so this interface is the only place all three share.
/// </remarks>
internal interface IXdcConsensusPeer : IXdcMessageContext
{
    protected XdcConsensusMessageHandler ConsensusMessages { get; }

    /// <summary>Sends a message to this peer.</summary>
    protected void Dispatch<T>(T message) where T : P2PMessage;

    void SendVote(Vote vote)
    {
        if (ConsensusMessages.ShouldNotify(vote))
            Dispatch(new VoteMsg { Vote = vote });
    }

    void SendTimeout(Timeout timeout)
    {
        if (ConsensusMessages.ShouldNotify(timeout))
            Dispatch(new TimeoutMsg { Timeout = timeout });
    }

    void SendSyncInfo(SyncInfo syncInfo) => Dispatch(new SyncInfoMsg { SyncInfo = syncInfo });
}
