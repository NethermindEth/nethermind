// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Maps a failed request's exception to the <see cref="PeerFailureReason"/> the peer is reported under.</summary>
/// <remarks>
/// A dead session must be reported as <see cref="PeerFailureReason.SessionClosed"/>, or the peer manager keeps
/// the zombie on a failure budget it can never work off; libp2p only surfaces that state through the message text.
/// </remarks>
internal static class PeerFailureClassifier
{
    public static PeerFailureReason Classify(Exception e) =>
        e.Message.Contains("Channel closed", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            ? PeerFailureReason.SessionClosed
            : PeerFailureReason.RequestFailed;
}
