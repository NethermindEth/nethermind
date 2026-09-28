// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.P2P;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>A dead session must classify as <see cref="PeerFailureReason.SessionClosed"/> so the peer manager drops it at once
/// instead of spending a failure budget on a peer that can never answer again.</summary>
public class PeerFailureClassifierTests
{
    [TestCase("Channel closed", PeerFailureReason.SessionClosed)]
    [TestCase("CHANNEL CLOSED by remote", PeerFailureReason.SessionClosed)]
    [TestCase("Session is no longer tracked by the libp2p layer", PeerFailureReason.SessionClosed)]
    [TestCase("One or more errors occurred. (Channel closed)", PeerFailureReason.SessionClosed)]
    [TestCase("Peer responded with error code 3: pruned", PeerFailureReason.RequestFailed)]
    [TestCase("", PeerFailureReason.RequestFailed)]
    public void Classifies_by_the_failure_message(string message, PeerFailureReason expected) =>
        Assert.That(PeerFailureClassifier.Classify(new Exception(message)), Is.EqualTo(expected));
}
