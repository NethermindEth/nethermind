// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.ReqResp.Protocols;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Maps a failed request's exception to the <see cref="PeerFailureReason"/> the peer is reported under.</summary>
internal static class PeerFailureClassifier
{
    public static PeerFailureReason Classify(Exception e)
    {
        if (e is AggregateException aggregate && aggregate.Flatten().InnerExceptions is { Count: > 0 } inner) e = inner[0];
        Exception cause = e is PartialBlocksException or PartialSidecarsException ? e.InnerException ?? e : e;
        if (cause is PeerSessionClosedException or Nethermind.Libp2p.Core.Exceptions.ChannelClosedException)
        {
            return PeerFailureReason.SessionClosed;
        }

        return cause is ReqRespTimeoutException { NotBlamed: true }
            ? PeerFailureReason.RequestNotBlamed
            : PeerFailureReason.RequestFailed;
    }
}

internal sealed class PeerSessionClosedException(string message, Exception? cause = null) : System.IO.IOException(message, cause);
