// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;

namespace Nethermind.BeaconChain.Test.P2P;

public class PeerFailureClassifierTests
{
    [Test]
    public void Peer_error_text_cannot_claim_a_closed_session([Values("Channel closed", "session limit", "pruned")] string message) =>
        Assert.That(PeerFailureClassifier.Classify(new Nethermind.BeaconChain.P2P.ReqResp.Eth2ReqRespException(message)), Is.EqualTo(PeerFailureReason.RequestFailed));

    [Test]
    public void A_closed_session_is_classified_by_its_type() =>
        Assert.That(PeerFailureClassifier.Classify(new PeerSessionClosedException("disconnected")), Is.EqualTo(PeerFailureReason.SessionClosed));

    [Test]
    public void An_aggregate_without_inner_exceptions_is_a_plain_request_failure() =>
        Assert.That(PeerFailureClassifier.Classify(new AggregateException()), Is.EqualTo(PeerFailureReason.RequestFailed));

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

        PeerFailureReason expected = notBlamed ? PeerFailureReason.RequestNotBlamed : PeerFailureReason.RequestFailed;
        Assert.That(PeerFailureClassifier.Classify(failure), Is.EqualTo(expected));
    }
}
