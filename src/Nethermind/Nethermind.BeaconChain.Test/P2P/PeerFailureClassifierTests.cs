// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;

namespace Nethermind.BeaconChain.Test.P2P;

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

    public enum Reply { None, PartialBlocks, PartialSidecars }

    [Test]
    public void Timeout_exemptions_belong_to_the_failed_request([Values] Reply reply, [Values] bool notBlamed, [Values] bool sessionClosed)
    {
        Exception timeout = new ReqRespTimeoutException(sessionClosed ? "its libp2p session closed" : "timed out") { NotBlamed = notBlamed };
        Exception failure = reply switch
        {
            Reply.None => timeout,
            Reply.PartialBlocks => new PartialBlocksException(timeout, []),
            Reply.PartialSidecars => new PartialSidecarsException(timeout, []),
            _ => throw new ArgumentOutOfRangeException(nameof(reply)),
        };

        PeerFailureReason expected = sessionClosed ? PeerFailureReason.SessionClosed
            : notBlamed ? PeerFailureReason.RequestNotBlamed : PeerFailureReason.RequestFailed;
        Assert.That(PeerFailureClassifier.Classify(failure), Is.EqualTo(expected));
    }
}
