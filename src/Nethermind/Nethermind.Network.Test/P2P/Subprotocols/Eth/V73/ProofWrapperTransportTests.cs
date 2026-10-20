// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

public class ProofWrapperTransportTests
{
    /// <summary>Serves the last GetProofWrappers sent to <paramref name="link"/> with <paramref name="wrappers"/>.</summary>
    private static void Answer(WrapperTestNode node, WrapperPeer peer, FakeWrapperLink link, params byte[][] wrappers) =>
        node.Transport.OnProofWrappers(peer, new ProofWrappersMessage(link.Sent<GetProofWrappersMessage>()[^1].RequestId, wrappers));

    /// <summary>The hashes of the <paramref name="index"/>-th envelope-recovery GetPooledTransactions sent to <paramref name="link"/>.</summary>
    private static (long RequestId, ValueHash256[] Hashes) EnvelopeRequest(FakeWrapperLink link, int index = 0)
    {
        GetPooledTransactionsMessage66 request = link.Sent<GetPooledTransactionsMessage66>()[index];
        return (request.RequestId, [.. request.EthMessage.Hashes]);
    }

    /// <summary>Publishes a wrapper from a node whose pool holds its transactions, and returns that node.</summary>
    private static WrapperTestNode Holder(byte[] wrapper, params Transaction[] transactions)
    {
        WrapperTestNode holder = new();
        holder.Pending.AddRange(transactions);
        Assert.That(holder.Transport.PublishWrapper(wrapper), Is.True);
        return holder;
    }

    [Test]
    public async Task Announced_wrapper_is_fetched_whole_recovers_envelopes_through_pooled_transactions_and_is_offered()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(1024, transaction);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode holder = Holder(wrapper, transaction);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new("source");
        WrapperPeer peer = node.Connect(link);
        FakeWrapperLink holderLink = new("requester");
        WrapperPeer requester = holder.Connect(holderLink);

        node.Transport.OnAnnounce(peer, new([announcement]));
        GetProofWrappersMessage request = link.Sent<GetProofWrappersMessage>().Single();
        Assert.That(request.Hashes, Is.EqualTo(new[] { announcement.Hash }));
        holder.Transport.OnGetProofWrappers(requester, request);
        node.Transport.OnProofWrappers(peer, holderLink.Sent<ProofWrappersMessage>().Single());

        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 1);
        (long requestId, ValueHash256[] requested) = EnvelopeRequest(link);
        Assert.That(requested, Is.EqualTo(new[] { transaction.Hash!.ValueHash256 }));
        // The holder serves the envelope from its wrapper-envelope cache even after its pool evicted the transaction.
        holder.Pending.Clear();
        Assert.That(holder.Transport.TryGetEnvelope(requested[0], out Transaction? served), Is.True);
        node.Transport.OnPooledEnvelopes(peer, requestId, [PooledItem(served!)]);

        await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash));
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pending.Select(t => t.Hash), Is.EqualTo(new[] { transaction.Hash }));
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(holderLink.Penalties, Is.Empty);
            Assert.That(node.Transport.ChargedBytes, Is.Zero, "every reservation is released once validation finishes");
            Assert.That(link.Sent<NewProofWrapperHashesMessage>(), Is.Empty, "the source already holds the wrapper");
        }
    }

    [Test]
    public void Wrapper_matching_no_requested_announcement_is_invalid_peer_data()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        byte[] wanted = Wrapper(64, FrameTransaction(1)), other = Wrapper(64, FrameTransaction(2));
        node.Transport.OnAnnounce(peer, new([Describe(wanted)]));
        Answer(node, peer, link, other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Has.Length.EqualTo(1));
            Assert.That(node.Transport.PeerCount, Is.Zero);
            Assert.That(node.Transport.ChargedBytes, Is.Zero);
        }
    }

    [Test]
    public void Response_to_an_unissued_request_is_a_violation_but_a_late_one_is_discarded()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(Wrapper(64, FrameTransaction(1)))]));
        long issued = link.Sent<GetProofWrappersMessage>().Single().RequestId;
        node.Tick(WrapperTransportLimits.RequestTimeout + TimeSpan.FromSeconds(1));
        node.Transport.OnProofWrappers(peer, new(issued, []));
        Assert.That(link.Penalties, Is.Empty, "a response after expiry is discarded");
        node.Transport.OnProofWrappers(peer, new(issued + 100, []));
        Assert.That(link.Penalties, Has.Length.EqualTo(1));
    }

    [Test]
    public void Skipped_wrappers_go_to_the_next_source_and_cut_ones_are_requested_again()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink a = new("a"), b = new("b");
        WrapperPeer peerA = node.Connect(a), peerB = node.Connect(b);
        byte[] first = Wrapper(64, FrameTransaction(1)), second = Wrapper(64, FrameTransaction(2)), third = Wrapper(64, FrameTransaction(3));
        WrapperAnnouncement[] announcements = [.. new[] { first, second, third }.Select(Describe).OrderBy(d => d.Hash)];
        node.Transport.OnAnnounce(peerA, new(announcements));
        node.Transport.OnAnnounce(peerB, new(announcements));
        GetProofWrappersMessage request = a.Sent<GetProofWrappersMessage>().Single();
        Assert.That(request.Hashes, Has.Length.EqualTo(3));
        byte[] Body(ValueHash256 id) => new[] { first, second, third }.Single(w => Describe(w).Hash == id);
        // The responder skipped the first wrapper and stopped after the second, as if at its soft limit.
        node.Transport.OnProofWrappers(peerA, new(request.RequestId, [Body(request.Hashes[1])]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Is.Empty);
            Assert.That(b.Sent<GetProofWrappersMessage>().Single().Hashes, Is.EqualTo(new[] { request.Hashes[0] }),
                "the skipped wrapper is unavailable from a, so b is asked");
            Assert.That(a.Sent<GetProofWrappersMessage>()[^1].Hashes, Is.EqualTo(new[] { request.Hashes[2] }),
                "the wrapper after the last one returned is requested again from a");
        }
    }

    // A response is held to the length its own server announced, so the first announcer's false length cannot get an
    // honest server penalized for the true wrapper.
    [Test]
    public void A_false_announced_length_does_not_blame_an_honest_server()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink liar = new("liar"), honest = new("honest");
        WrapperPeer liarPeer = node.Connect(liar), honestPeer = node.Connect(honest);
        byte[] wrapper = Wrapper(64, FrameTransaction(1));
        WrapperAnnouncement truth = Describe(wrapper);
        WrapperAnnouncement lie = WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)truth.Mode),
            Eth73Rlp.EncodeUInt((ulong)wrapper.Length + 1), Eth73Rlp.EncodeBytes(truth.Hash.Bytes)));
        node.Transport.OnAnnounce(liarPeer, new([lie]));
        node.Transport.OnAnnounce(honestPeer, new([truth]));
        node.Transport.OnProofWrappers(liarPeer, new(liar.Sent<GetProofWrappersMessage>().Single().RequestId, []));
        node.Transport.OnProofWrappers(honestPeer, new(honest.Sent<GetProofWrappersMessage>().Single().RequestId, [wrapper]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(honest.Penalties, Is.Empty);
            Assert.That(liar.Penalties, Is.Empty, "an empty response is not misbehavior");
        }
    }

    [Test]
    public void One_silent_announcer_cannot_fill_the_pending_cache_for_other_peers()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink silent = new("silent"), honest = new("honest");
        WrapperPeer silentPeer = node.Connect(silent), honestPeer = node.Connect(honest);
        int announced = 0;
        for (int message = 0; message < 6; message++)
        {
            WrapperAnnouncement[] batch = [.. Enumerable.Range(0, Eth73Protocol.MaxAnnouncements)
                .Select(_ => WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeUInt(64),
                    Eth73Rlp.EncodeBytes(Keccak.Compute($"silent:{announced++}").Bytes))))
                .OrderBy(a => a.Hash)];
            node.Transport.OnAnnounce(silentPeer, new(batch));
            node.Tick(TimeSpan.FromSeconds(1));
        }
        Assert.That(node.Transport.FetchCount, Is.EqualTo(WrapperTransportLimits.MaxPendingAnnouncementsPerPeer));
        byte[] wrapper = Wrapper(64, FrameTransaction(1));
        node.Transport.OnAnnounce(honestPeer, new([Describe(wrapper)]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.FetchCount, Is.EqualTo(WrapperTransportLimits.MaxPendingAnnouncementsPerPeer + 1));
            Assert.That(honest.Sent<GetProofWrappersMessage>().Single().Hashes, Is.EqualTo(new[] { Describe(wrapper).Hash }));
        }
    }

    [Test]
    public void Requests_stay_within_four_outstanding_and_the_soft_response_limit()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        WrapperAnnouncement[] announcements = [.. Enumerable.Range(0, 10)
            .Select(i => Describe(Wrapper(Eth73Protocol.SoftResponseLimit / 3, FrameTransaction(i)))).OrderBy(d => d.Hash)];
        node.Transport.OnAnnounce(peer, new(announcements));
        GetProofWrappersMessage[] requests = link.Sent<GetProofWrappersMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Has.Length.EqualTo(WrapperTransportLimits.MaxRequestsPerPeer));
            Assert.That(requests.All(r => r.Hashes.Length == 2), Is.True, "two wrappers of a third of the soft limit fit, a third does not");
            Assert.That(node.Transport.ChargedBytes, Is.LessThanOrEqualTo(WrapperTransportLimits.MaxPeerBytes));
        }
    }

    // EIP-8437 18a4d088: a responder adds no wrapper once the response reaches the soft limit, so the last one added may
    // take it past the limit; check_vectors.py packs three of four 1 MB wrappers.
    [Test]
    public void Responder_returns_held_wrappers_in_request_order_until_the_soft_limit_is_reached()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2), c = FrameTransaction(3);
        byte[] first = Wrapper(Eip8288Constants.MaxAggregateProofBytes, a), second = Wrapper(Eip8288Constants.MaxAggregateProofBytes - 1, b),
            third = Wrapper(Eip8288Constants.MaxAggregateProofBytes - 2, c);
        using WrapperTestNode node = Holder(first, a);
        node.Pending.AddRange([b, c]);
        Assert.That(node.Transport.PublishWrapper(second), Is.True);
        Assert.That(node.Transport.PublishWrapper(third), Is.True);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Tick(TimeSpan.FromSeconds(1));
        ValueHash256 unknown = TestItem.KeccakA.ValueHash256;
        node.Transport.OnGetProofWrappers(peer, new(7, [unknown, Describe(second).Hash, Describe(first).Hash, Describe(third).Hash]));
        node.Transport.OnGetProofWrappers(peer, new(8, [unknown]));
        ProofWrappersMessage[] responses = link.Sent<ProofWrappersMessage>();
        int length = new ProofWrappersMessageSerializer().Encode(responses[0]).Length;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses[0].RequestId, Is.EqualTo(7));
            Assert.That(responses[0].Wrappers, Is.EqualTo(new[] { second, first }),
                "unknown IDs are skipped, the second wrapper overshoots the soft limit and the third is not added");
            Assert.That(responses[1].Wrappers, Is.Empty);
            Assert.That(length, Is.GreaterThan(Eth73Protocol.SoftResponseLimit).And.LessThanOrEqualTo(Eth73Protocol.LocalMaxResponseBytes));
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, length), Is.False, "overshooting the soft limit is valid");
        }
    }

    // EIP-8437: while offering a wrapper a peer keeps every listed envelope available, including ones its pool rejected or
    // evicted, looking only in the pool and a bounded wrapper-envelope cache.
    [Test]
    public void Wrapper_envelope_cache_serves_listed_envelopes_after_eviction_and_nothing_else()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = Holder(wrapper, transaction);
        node.Pending.Clear();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.TryGetEnvelope(transaction.Hash!.ValueHash256, out Transaction? served), Is.True);
            Assert.That(Envelope(served!), Is.EqualTo(Envelope(transaction)));
            Assert.That(node.Transport.TryGetEnvelope(FrameTransaction(2).Hash!.ValueHash256, out _), Is.False);
        }
    }

    // EIP-8437: a PooledTransactions entry is matched by its transaction hash, excluding any network wrapper, so a blob
    // transaction served with its sidecar still matches.
    [Test]
    public void Pooled_items_are_matched_by_transaction_hash_without_the_network_wrapper()
    {
        Transaction blob = Build.A.Transaction.WithShardBlobTxTypeAndFields(spec: Cancun.Instance).SignedAndResolved().TestObject;
        byte[] item = PooledItem(blob);
        Transaction? decoded = ProofWrapperTransport.DecodePooled(item);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded?.Hash, Is.EqualTo(blob.Hash));
            Assert.That(ValueKeccak.Compute(item), Is.Not.EqualTo(blob.Hash!.ValueHash256), "the item is not the bare envelope");
            Assert.That(ProofWrapperTransport.DecodePooled(PooledItem(FrameTransaction(1)))?.Hash, Is.EqualTo(FrameTransaction(1).Hash));
        }
    }

    // assets/eip-8437/check_vectors.py check_envelopes: omissions and truncation are allowed; repeats, reordering and
    // unrequested entries are not.
    [TestCase(new[] { 0, 1, 2 }, true)]
    [TestCase(new[] { 0, 2 }, true)]
    [TestCase(new[] { 2 }, true)]
    [TestCase(new[] { 0 }, true)]
    [TestCase(new int[0], true)]
    [TestCase(new[] { 0, 0 }, false)]
    [TestCase(new[] { 2, 0 }, false)]
    [TestCase(new[] { 3 }, false)]
    [TestCase(new[] { 0, 1, 2, 0 }, false)]
    public void Pooled_envelopes_match_distinct_requested_hashes_in_order(int[] returned, bool matches)
    {
        ValueHash256[] requested = [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256, TestItem.KeccakC.ValueHash256];
        ValueHash256 unrequested = TestItem.KeccakD.ValueHash256;
        ValueHash256[] hashes = [.. returned.Select(i => i < requested.Length ? requested[i] : unrequested)];
        Assert.That(ProofWrapperTransport.MatchesInOrder(hashes, requested), Is.EqualTo(matches));
    }

    [TestCase("unrequested")]
    [TestCase("repeated")]
    [TestCase("reordered")]
    [TestCase("malformed")]
    public async Task Repeated_reordered_unrequested_or_malformed_envelopes_are_unmatched_data(string scenario)
    {
        Transaction first = FrameTransaction(1), second = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, first, second);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 1);
        (long requestId, ValueHash256[] requested) = EnvelopeRequest(link);
        byte[] Of(ValueHash256 hash) => PooledItem(hash == first.Hash!.ValueHash256 ? first : second);
        byte[][] envelopes = scenario switch
        {
            "unrequested" => [PooledItem(FrameTransaction(3))],
            "malformed" => [[0xc0]],
            "repeated" => [Of(requested[0]), Of(requested[0])],
            _ => [Of(requested[1]), Of(requested[0])]
        };
        node.Transport.OnPooledEnvelopes(peer, requestId, envelopes);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Is.Not.Empty);
            Assert.That(node.Pending, Is.Empty);
            Assert.That(node.Transport.IsStored(Describe(wrapper).Hash), Is.False);
        }
    }

    // devp2p PooledTransactions: entries skipped before the last returned one are unavailable from that peer, those after it
    // were cut by the response limit and are requested again.
    [TestCase(true)]
    [TestCase(false)]
    public async Task Omitted_envelopes_go_to_the_next_source_and_truncated_ones_to_the_same(bool omitFirst)
    {
        Transaction first = FrameTransaction(1), second = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, first, second);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new();
        FakeWrapperLink a = new("a"), b = new("b");
        WrapperPeer peerA = node.Connect(a), peerB = node.Connect(b);
        node.Transport.OnAnnounce(peerA, new([announcement]));
        node.Transport.OnAnnounce(peerB, new([announcement]));
        Answer(node, peerA, a, wrapper);
        await WrapperTestNode.Until(() => a.Sent<GetPooledTransactionsMessage66>().Length == 1);
        (long requestId, ValueHash256[] requested) = EnvelopeRequest(a);
        Transaction Of(ValueHash256 hash) => hash == first.Hash!.ValueHash256 ? first : second;
        node.Transport.OnPooledEnvelopes(peerA, requestId, [PooledItem(Of(requested[omitFirst ? 1 : 0]))]);
        FakeWrapperLink next = omitFirst ? b : a;
        int sent = omitFirst ? 1 : 2;
        await WrapperTestNode.Until(() => next.Sent<GetPooledTransactionsMessage66>().Length == sent);
        (long retryId, ValueHash256[] retried) = EnvelopeRequest(next, sent - 1);
        ValueHash256 remaining = requested[omitFirst ? 0 : 1];
        Assert.That(retried, Is.EqualTo(new[] { remaining }));
        node.Transport.OnPooledEnvelopes(omitFirst ? peerB : peerA, retryId, [PooledItem(Of(remaining))]);
        await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash) && node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Is.Empty);
            Assert.That(b.Penalties, Is.Empty);
            Assert.That(node.Pending.Select(t => t.Hash), Is.EquivalentTo(new[] { first.Hash, second.Hash }));
        }
    }

    [Test]
    public async Task Empty_envelope_response_tries_another_announcer_without_penalty()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new();
        FakeWrapperLink a = new("a"), b = new("b");
        WrapperPeer peerA = node.Connect(a), peerB = node.Connect(b);
        node.Transport.OnAnnounce(peerA, new([announcement]));
        node.Transport.OnAnnounce(peerB, new([announcement]));
        Answer(node, peerA, a, wrapper);
        await WrapperTestNode.Until(() => a.Sent<GetPooledTransactionsMessage66>().Length == 1);
        node.Transport.OnPooledEnvelopes(peerA, EnvelopeRequest(a).RequestId, []);
        await WrapperTestNode.Until(() => b.Sent<GetPooledTransactionsMessage66>().Length == 1);
        node.Transport.OnPooledEnvelopes(peerB, EnvelopeRequest(b).RequestId, [PooledItem(transaction)]);
        await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Is.Empty);
            Assert.That(b.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task Only_outstanding_envelope_requests_are_claimed_by_the_transport()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        long wrapperRequest = link.Sent<GetProofWrappersMessage>().Single().RequestId;
        Assert.That(node.Transport.IsEnvelopeRequest(peer, wrapperRequest), Is.False, "a GetProofWrappers ID is not an envelope request");
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 1);
        long requestId = EnvelopeRequest(link).RequestId;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsEnvelopeRequest(peer, requestId), Is.True);
            Assert.That(node.Transport.IsEnvelopeRequest(peer, requestId + 1000), Is.False, "other PooledTransactions stay with the eth/72 handling");
        }
        node.Tick(WrapperTransportLimits.RequestTimeout + TimeSpan.FromSeconds(1));
        Assert.That(node.Transport.IsEnvelopeRequest(peer, requestId), Is.False, "an expired request is no longer claimed");
    }

    [Test]
    public async Task A_throwing_claims_check_stops_envelope_recovery()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new();
        node.Verifier.Throws = new InvalidOperationException("verifier fault");
        FakeWrapperLink a = new("a"), b = new("b");
        WrapperPeer peerA = node.Connect(a), peerB = node.Connect(b);
        node.Transport.OnAnnounce(peerA, new([announcement]));
        node.Transport.OnAnnounce(peerB, new([announcement]));
        Answer(node, peerA, a, wrapper);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        node.Tick(WrapperTransportLimits.RequestTimeout + TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Sent<GetPooledTransactionsMessage66>(), Has.Length.AtMost(1));
            Assert.That(b.Sent<GetPooledTransactionsMessage66>(), Is.Empty, "a cancelled recovery asks no other announcer");
            Assert.That(a.Penalties, Is.Empty, "a local verifier fault blames no peer");
            Assert.That(node.Pending, Is.Empty);
        }
    }

    [Test]
    public async Task Invalid_proof_is_tombstoned_and_its_server_penalized()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = new();
        node.Verifier.Valid = false;
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Has.Length.EqualTo(1));
            Assert.That(node.Pending, Is.Empty, "no transaction is admitted under an invalid proof");
            Assert.That(node.Transport.IsTombstoned(Describe(wrapper).Hash), Is.True);
        }
    }

    [Test]
    public async Task Pool_rejected_wrapper_is_soft_tombstoned_without_penalty_and_not_refetched()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = new();
        node.Pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.FeeTooLow);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 1);
        node.Transport.OnPooledEnvelopes(peer, EnvelopeRequest(link).RequestId, [PooledItem(transaction)]);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);

        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(node.Transport.IsStored(Describe(wrapper).Hash), Is.False);
            Assert.That(link.Sent<GetProofWrappersMessage>(), Has.Length.EqualTo(1), "the re-announcement is not fetched again");
        }
    }

    private static readonly ValueHash256 UpgradedVk = ValueKeccak.Compute("next AGGREGATED_VK");

    /// <summary>A chain whose fork at timestamp 1,000 changes <c>AGGREGATED_VK</c>.</summary>
    private static ISpecProvider UpgradingChain() => new CustomSpecProvider(((ForkActivation)0, Eip8288Prototype.Instance),
        (new ForkActivation(0, 1_000), new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = UpgradedVk }));

    /// <summary>Moves the local head so the next slot's fork changes the verifier, and lets the transport apply it.</summary>
    private static void CrossVerifierFork(WrapperTestNode node, ulong number = 10)
    {
        node.SetHead(number, 990);
        node.Tick(WrapperTransportLimits.Tick);
    }

    // EIP-8437 / EIP-8288 upgrade rule: once the target fork's verifier changes, every mode-1 wrapper is dropped and never
    // announced or served again, while a mode-0 wrapper whose direct witnesses re-validate stays offered.
    [Test]
    public async Task Verifier_change_drops_aggregate_wrappers_and_keeps_revalidated_direct_ones()
    {
        Transaction aggregated = FrameTransaction(1);
        Transaction direct = FrameTransaction(2);
        byte[] mode1 = Wrapper(64, aggregated);
        byte[] mode0 = DirectWrapper(direct);
        using WrapperTestNode node = new(UpgradingChain());
        node.SetHead(10, 100);
        node.Pending.AddRange([aggregated, direct]);
        Assert.That(node.Transport.PublishWrapper(mode1), Is.True);
        Assert.That(node.Transport.PublishWrapper(mode0), Is.True);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);

        CrossVerifierFork(node);
        await WrapperTestNode.Until(() => !node.Transport.IsStored(Describe(mode1).Hash));
        node.Transport.OnGetProofWrappers(peer, new GetProofWrappersMessage(1, [Describe(mode1).Hash, Describe(mode0).Hash]));
        FakeWrapperLink late = new("late");
        node.Connect(late);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsOffered(Describe(mode0).Hash), Is.True);
            Assert.That(link.Sent<ProofWrappersMessage>().Single().Wrappers, Is.EqualTo(new[] { mode0 }), "the dropped wrapper is not served");
            Assert.That(late.Sent<NewProofWrapperHashesMessage>().Single().Announcements.Select(d => d.Hash),
                Is.EqualTo(new[] { Describe(mode0).Hash }), "nor announced");
            // EIP-8437 18a4d088: suppression records reset at the verifier change, so the revalidated wrapper is announced
            // again on the same connection.
            Assert.That(link.Sent<NewProofWrapperHashesMessage>()[^1].Announcements.Select(d => d.Hash),
                Is.EqualTo(new[] { Describe(mode0).Hash }));
            Assert.That(node.Transport.PublishWrapper(mode1), Is.True, "re-validated under the new verifier, it is offered again");
        }
    }

    [Test]
    public void Upgrade_rule_fault_on_the_timer_is_contained_and_retried()
    {
        using WrapperTestNode node = new(UpgradingChain());
        node.SetHead(10, 100);
        node.Wrappers.RefreshVerifier();
        int changes = 0;
        node.Wrappers.VerifierChanged += _ =>
        {
            if (++changes == 1) throw new InvalidOperationException("native fault");
        };

        Assert.That(() => CrossVerifierFork(node), Throws.Nothing, "a fault must not escape the timer callback");
        node.Tick(WrapperTransportLimits.Tick);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.EqualTo(2), "the faulted upgrade rule runs again on the next tick");
            Assert.That(node.Wrappers.CurrentVerifier, Is.EqualTo(new Eip8288Verifier(UpgradedVk)));
        }
    }

    // EIP-8437 Errors: around a fork that changes the verifier, a peer's proof failure is a local refusal within the configured
    // grace window (default until the head is 64 blocks past the fork), and invalid peer data after it.
    [TestCase(74UL, false)]
    [TestCase(75UL, true)]
    public async Task Proof_failures_within_the_fork_grace_window_are_local_refusals(ulong headNumber, bool penalized)
    {
        using WrapperTestNode node = new(UpgradingChain());
        node.SetHead(10, 100);
        node.Wrappers.RefreshVerifier();
        CrossVerifierFork(node);
        node.SetHead(headNumber, 1_800);
        node.Verifier.Valid = false;
        byte[] wrapper = Wrapper(64, FrameTransaction(1));
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);

        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Has.Length.EqualTo(penalized ? 1 : 0));
            Assert.That(node.Transport.IsTombstoned(Describe(wrapper).Hash), Is.True);
        }
    }

    [Test]
    public void Peers_are_not_registered_while_eip8288_is_inactive()
    {
        using WrapperTestNode node = new();
        node.Tree.Head.Returns((Block?)null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.Accept(new FakeWrapperLink()), Is.Null);
            Assert.That(node.Transport.PeerCount, Is.Zero);
        }
    }

    [Test]
    public void Published_wrappers_are_announced_once_within_the_rate_limit_and_to_new_peers()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink early = new("early");
        WrapperPeer peer = node.Connect(early);
        Transaction[] transactions = [.. Enumerable.Range(0, 6).Select(FrameTransaction)];
        node.Pending.AddRange(transactions);
        foreach (Transaction transaction in transactions) Assert.That(node.Transport.PublishWrapper(Wrapper(64, transaction)), Is.True);
        Assert.That(early.Sent<NewProofWrapperHashesMessage>(), Has.Length.EqualTo(WrapperTransportLimits.AnnouncementsPerSecond));
        node.Tick(TimeSpan.FromSeconds(1));
        NewProofWrapperHashesMessage[] announcements = early.Sent<NewProofWrapperHashesMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcements.Sum(a => a.Announcements.Length), Is.EqualTo(6));
            Assert.That(announcements.SelectMany(a => a.Announcements).Select(d => d.Hash).Distinct().Count(), Is.EqualTo(6));
        }
        FakeWrapperLink late = new("late");
        node.Connect(late);
        NewProofWrapperHashesMessage initial = late.Sent<NewProofWrapperHashesMessage>().Single();
        Assert.That(initial.Announcements, Has.Length.EqualTo(6));
        Assert.That(initial.Announcements.Select(d => d.Hash), Is.Ordered);
    }

    [Test]
    public void Publishing_requires_every_listed_envelope()
    {
        using WrapperTestNode node = new();
        Transaction transaction = FrameTransaction(1);
        Assert.That(node.Transport.PublishWrapper(Wrapper(64, transaction)), Is.False);
        node.Pending.Add(transaction);
        Assert.That(node.Transport.PublishWrapper(Wrapper(64, transaction)), Is.True);
    }
}
