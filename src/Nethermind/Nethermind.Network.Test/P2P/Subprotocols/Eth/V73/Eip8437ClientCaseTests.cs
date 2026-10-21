// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.TxPool;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>The client cases EIP-8437 lists under Test Cases, with valid dependency-bearing transactions A and B and a covering
/// wrapper W under a fixed target verifier.</summary>
public class Eip8437ClientCaseTests
{
    private static void Answer(WrapperTestNode node, WrapperPeer peer, FakeWrapperLink link, params byte[][] wrappers) =>
        node.Transport.OnProofWrappers(peer, new ProofWrappersMessage(link.Sent<GetProofWrappersMessage>()[^1].RequestId, wrappers));

    private static (long RequestId, ValueHash256[] Hashes) EnvelopeRequest(FakeWrapperLink link, int index = 0)
    {
        GetPooledTransactionsMessage66 request = link.Sent<GetPooledTransactionsMessage66>()[index];
        return (request.RequestId, [.. request.EthMessage.Hashes]);
    }

    // Case 1: retain A and B received through Transactions, then announce, request and receive W. No admission or relay before
    // full wrapper validation; afterward the proof requirement is satisfied.
    [Test]
    public async Task Envelopes_pushed_before_their_wrapper_are_admitted_only_once_it_validates()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, a, b);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);

        node.Transport.RetainEnvelope(peer, a);
        node.Transport.RetainEnvelope(peer, b);
        Assert.That(node.Pending, Is.Empty, "a pushed envelope is not admitted on its own");
        Assert.That(node.Transport.RetainedEnvelopeCount, Is.EqualTo(2));

        node.Transport.OnAnnounce(peer, new([announcement]));
        Assert.That(node.Pending, Is.Empty, "an announcement admits nothing");
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash) && node.Transport.FetchCount == 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pending.Select(t => t.Hash), Is.EquivalentTo(new[] { a.Hash, b.Hash }));
            Assert.That(link.Sent<GetPooledTransactionsMessage66>(), Is.Empty, "retained envelopes stand in for recovery");
            Assert.That(node.Transport.RetainedEnvelopeCount, Is.Zero, "covered envelopes move to the wrapper-envelope cache");
            Assert.That(node.Transport.TryGetEnvelope(a.Hash!.ValueHash256, out _), Is.True);
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    // Case 2: receive W first; recover A, then B through GetPooledTransactions. W stays incomplete after A; after B it
    // validates to the same result as case 1.
    [Test]
    public async Task Wrapper_received_first_stays_incomplete_until_every_envelope_is_recovered()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, a, b);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([announcement]));
        Answer(node, peer, link, wrapper);

        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 1);
        (long first, ValueHash256[] requested) = EnvelopeRequest(link);
        Transaction Of(ValueHash256 hash) => hash == a.Hash!.ValueHash256 ? a : b;
        node.Transport.OnPooledEnvelopes(peer, first, [PooledItem(Of(requested[0]))]);
        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsStored(announcement.Hash), Is.False, "incomplete, not invalid");
            Assert.That(node.Transport.IsTombstoned(announcement.Hash), Is.False);
            Assert.That(node.Pending, Is.Empty);
        }

        (long second, ValueHash256[] rest) = EnvelopeRequest(link, 1);
        Assert.That(rest, Is.EqualTo(new[] { requested[1] }));
        node.Transport.OnPooledEnvelopes(peer, second, [PooledItem(Of(rest[0]))]);
        await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash) && node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pending.Select(t => t.Hash), Is.EquivalentTo(new[] { a.Hash, b.Hash }));
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    // Case 3: A, B and a wrapper with an invalid proof but correctly announced mode, length and hash. Transport matching
    // passes; proof validation fails and nothing is admitted, relayed or reaggregated from it.
    [Test]
    public async Task Correctly_announced_wrapper_with_an_invalid_proof_matches_but_fails_validation()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, a, b);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new();
        node.Verifier.Valid = false;
        FakeWrapperLink link = new(), other = new("other");
        WrapperPeer peer = node.Connect(link);
        node.Connect(other);
        node.Transport.RetainEnvelope(peer, a);
        node.Transport.RetainEnvelope(peer, b);
        node.Transport.OnAnnounce(peer, new([announcement]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.IsTombstoned(announcement.Hash));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties.Single(), Does.Contain("served invalid"), "the response matched; the proof failed");
            Assert.That(node.Pending, Is.Empty);
            Assert.That(node.Transport.IsStored(announcement.Hash), Is.False, "never offered or reaggregated");
            Assert.That(other.Sent<NewProofWrapperHashesMessage>(), Is.Empty, "never relayed");
        }
    }

    // Case 4: validate W, evict A while keeping its envelope in bounded storage, keep offering W and answer GetPooledTransactions
    // for A from that storage.
    [Test]
    public void Envelope_of_an_evicted_transaction_is_served_while_its_wrapper_is_offered()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, a, b);
        using WrapperTestNode node = new();
        node.Pending.AddRange([a, b]);
        Assert.That(node.Transport.PublishWrapper(wrapper), Is.True);
        node.Pending.Remove(a);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsOffered(Describe(wrapper).Hash), Is.True);
            Assert.That(node.Transport.TryGetEnvelope(a.Hash!.ValueHash256, out Transaction? served), Is.True);
            Assert.That(Envelope(served!), Is.EqualTo(Envelope(a)));
        }
    }

    // Case 5: request A by its hash in W without a NewPooledTransactionHashes announcement; repeat after the serving peer
    // advertises an incorrect type or size for A. No announcement is required; the disagreeing response is rejected.
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task Recovered_envelopes_need_no_announcement_but_must_match_an_announced_shape(bool wrongType, bool wrongSize)
    {
        Transaction a = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, a);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        bool announced = wrongType || wrongSize;
        if (announced) link.AnnouncedShapes[a.Hash!] = (wrongType ? TxType.EIP1559 : a.Type, a.GetLength() + (wrongSize ? 1 : 0));
        node.Transport.OnAnnounce(peer, new([announcement]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 1);
        node.Transport.OnPooledEnvelopes(peer, EnvelopeRequest(link).RequestId, [PooledItem(a)]);

        if (!announced)
        {
            await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash));
            Assert.That(link.Penalties, Is.Empty);
            return;
        }
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties.Single(), Does.Contain("announced transaction type or size"));
            Assert.That(node.Transport.IsStored(announcement.Hash), Is.False);
            Assert.That(node.Pending, Is.Empty);
        }
    }

    // Case 6: a peer announces mode-1 W1 covering A and W2 covering B. W3, mode 1 over both, is announced because it combines
    // two proofs into one. After W3 is sent to the peer, W4 with the same transactions, mode and size is not announced.
    [Test]
    public void Redundant_coverage_is_announced_only_when_it_reduces_verification_work()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] w1 = Wrapper(64, a), w2 = Wrapper(64, b), w3 = Wrapper(64, 3, a, b), w4 = Wrapper(64, 4, a, b);
        Assert.That(Describe(w4).Hash, Is.Not.EqualTo(Describe(w3).Hash));
        using WrapperTestNode node = new();
        node.Pending.AddRange([a, b]);
        FakeWrapperLink link = new(), fresh = new("fresh");
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([.. new[] { Describe(w1), Describe(w2) }.OrderBy(d => d.Hash)]));
        Assert.That(node.Transport.PublishWrapper(w1), Is.True);
        Assert.That(node.Transport.PublishWrapper(w2), Is.True);
        Assert.That(node.Transport.PublishWrapper(w3), Is.True);
        static ValueHash256[] AnnouncedTo(FakeWrapperLink target) =>
            [.. target.Sent<NewProofWrapperHashesMessage>().SelectMany(m => m.Announcements).Select(d => d.Hash)];
        Assert.That(AnnouncedTo(link), Is.EqualTo(new[] { Describe(w3).Hash }), "W1 and W2 came from the peer; W3 halves its verifications");

        node.Transport.OnGetProofWrappers(peer, new GetProofWrappersMessage(1, [Describe(w3).Hash]));
        Assert.That(link.Sent<ProofWrappersMessage>().Single().Wrappers, Is.EqualTo(new[] { w3 }));
        node.Tick(TimeSpan.FromSeconds(1));
        Assert.That(node.Transport.PublishWrapper(w4), Is.True);
        node.Tick(TimeSpan.FromSeconds(1));
        WrapperPeer freshPeer = node.Connect(fresh);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AnnouncedTo(link), Is.EqualTo(new[] { Describe(w3).Hash }), "W4 adds nothing the peer's W3 lacks");
            Assert.That(AnnouncedTo(fresh), Does.Contain(Describe(w4).Hash), "a peer without that coverage still learns of W4");
            Assert.That(freshPeer, Is.Not.Null);
        }
    }

    // Case 7: a peer repeatedly pushes envelopes but announces no covering wrapper before the retention window expires, with
    // all its announcements resolved: its score is lowered. With a covering wrapper announced but not fetched, or recovery
    // blocked by local limits, it is not.
    [TestCase("no covering wrapper", true)]
    [TestCase("covering wrapper announced but not fetched", false)]
    [TestCase("recovery blocked by a local limit", false)]
    public async Task Envelopes_pushed_without_a_covering_wrapper_lower_the_sender_score(string scenario, bool lowered)
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] covering = Wrapper(64, a, b);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.RetainEnvelope(peer, a);
        node.Transport.RetainEnvelope(peer, b);
        switch (scenario)
        {
            case "covering wrapper announced but not fetched":
                node.Transport.OnAnnounce(peer, new([Describe(covering)]));
                break;
            case "recovery blocked by a local limit":
                node.Verifier.Throws = new InvalidOperationException("verification capacity exhausted");
                Transaction c = FrameTransaction(3);
                byte[] other = Wrapper(64, c);
                node.Transport.OnAnnounce(peer, new([Describe(other)]));
                Answer(node, peer, link, other);
                await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
                break;
        }
        node.Tick(WrapperTransportLimits.EnvelopeRetention + TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.RetainedEnvelopeCount, Is.Zero, "retention is finite");
            Assert.That(link.ScoreReductions, lowered ? Has.Length.EqualTo(2) : Is.Empty);
            Assert.That(link.Penalties, Is.Empty, "a lowered score is not a disconnect");
            Assert.That(node.Pending, Is.Empty);
        }
    }

    // A dependency-bearing envelope already covered locally, or beyond the sender's share, is not retained twice.
    [Test]
    public void Retention_is_bounded_per_peer_and_skips_covered_envelopes()
    {
        Transaction a = FrameTransaction(1);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Pending.Add(a);
        Assert.That(node.Transport.PublishWrapper(Wrapper(64, a)), Is.True);
        node.Transport.RetainEnvelope(peer, a);
        Assert.That(node.Transport.RetainedEnvelopeCount, Is.Zero, "an envelope a held wrapper lists is already kept");
        for (int i = 0; i < WrapperTransportLimits.MaxRetainedEnvelopesPerPeer + 5; i++) node.Transport.RetainEnvelope(peer, FrameTransaction(100 + i));
        Assert.That(node.Transport.RetainedEnvelopeCount, Is.EqualTo(WrapperTransportLimits.MaxRetainedEnvelopesPerPeer));
        node.Tick(WrapperTransportLimits.EnvelopeRetention + TimeSpan.FromSeconds(1));
        Assert.That(link.ScoreReductions, Is.Empty, "the receiver's own limit spared the sender");
    }

    // A response must match the responding peer's announced mode as well as its length and hash.
    [Test]
    public void Wrapper_with_another_mode_than_announced_is_unmatched_data()
    {
        Transaction a = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, a);
        WrapperAnnouncement truth = Describe(wrapper);
        WrapperAnnouncement wrongMode = WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)(1 - truth.Mode)),
            Eth73Rlp.EncodeUInt((ulong)truth.Length), Eth73Rlp.EncodeBytes(truth.Hash.Bytes)));
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([wrongMode]));
        Answer(node, peer, link, wrapper);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties.Single(), Does.Contain("matches no requested announcement"));
            Assert.That(node.Transport.ChargedBytes, Is.Zero);
        }
    }

    // GetProofWrappers names wrappers the recipient announced; others are answered as unavailable.
    [Test]
    public void Unannounced_wrappers_are_not_served()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] announced = Wrapper(64, a), suppressed = Wrapper(64, b);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Pending.AddRange([a, b]);
        Assert.That(node.Transport.PublishWrapper(announced), Is.True);
        // Over the announcement rate limit, so the second wrapper is still queued.
        for (int i = 0; i < WrapperTransportLimits.AnnouncementsPerSecond; i++) node.Transport.PublishWrapper(Wrapper(64, 10 + i, a));
        Assert.That(node.Transport.PublishWrapper(suppressed), Is.True);
        node.Transport.OnGetProofWrappers(peer, new GetProofWrappersMessage(9, [Describe(announced).Hash, Describe(suppressed).Hash]));
        Assert.That(link.Sent<ProofWrappersMessage>().Single().Wrappers, Is.EqualTo(new[] { announced }));
    }
}
