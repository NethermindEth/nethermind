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
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

public class ProofWrapperTransportTests
{
    /// <summary>Serves the last GetProofWrappers sent to <paramref name="link"/> with <paramref name="wrappers"/>.</summary>
    private static void Answer(WrapperTestNode node, WrapperPeer peer, FakeWrapperLink link, params byte[][] wrappers) =>
        node.Transport.OnProofWrappers(peer, new ProofWrappersMessage(link.Sent<GetProofWrappersMessage>()[^1].RequestId, wrappers));

    /// <summary>Publishes a wrapper from a node whose pool holds its transactions, and returns that node.</summary>
    private static WrapperTestNode Holder(byte[] wrapper, params Transaction[] transactions)
    {
        WrapperTestNode holder = new();
        holder.Pending.AddRange(transactions);
        Assert.That(holder.Transport.PublishWrapper(wrapper), Is.True);
        return holder;
    }

    [Test]
    public async Task Announced_wrapper_is_fetched_whole_recovers_envelopes_and_is_offered_after_validation()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(1024, transaction);
        WrapperDescriptor descriptor = Describe(wrapper);
        using WrapperTestNode holder = Holder(wrapper, transaction);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new("source");
        WrapperPeer peer = node.Connect(link);
        FakeWrapperLink holderLink = new("requester");
        WrapperPeer requester = holder.Connect(holderLink);

        node.Transport.OnAnnounce(peer, new([descriptor]));
        GetProofWrappersMessage request = link.Sent<GetProofWrappersMessage>().Single();
        Assert.That(request.ObjectIds, Is.EqualTo(new[] { descriptor.ObjectId }));
        holder.Transport.OnGetProofWrappers(requester, request);
        node.Transport.OnProofWrappers(peer, holderLink.Sent<ProofWrappersMessage>().Single());

        await WrapperTestNode.Until(() => link.Sent<GetWrapperEnvelopesMessage>().Length == 1);
        GetWrapperEnvelopesMessage envelopes = link.Sent<GetWrapperEnvelopesMessage>().Single();
        Assert.That(envelopes.Indices, Is.EqualTo(new[] { 0 }));
        // The holder serves the envelope even after its pool evicted the transaction.
        holder.Pending.Clear();
        holder.Transport.OnGetWrapperEnvelopes(requester, envelopes);
        node.Transport.OnWrapperEnvelopes(peer, holderLink.Sent<WrapperEnvelopesMessage>().Single());

        await WrapperTestNode.Until(() => node.Transport.IsStored(descriptor.ObjectId));
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pending.Select(t => t.Hash), Is.EqualTo(new[] { transaction.Hash }));
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(holderLink.Penalties, Is.Empty);
            Assert.That(node.Transport.ChargedBytes, Is.Zero, "every reservation is released once validation finishes");
            Assert.That(link.Sent<AnnounceProofWrappersMessage>(), Is.Empty, "the source already holds the wrapper");
        }
    }

    [Test]
    public void Wrapper_matching_no_requested_descriptor_is_invalid_peer_data()
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
        WrapperDescriptor[] descriptors = [.. new[] { first, second, third }.Select(Describe).OrderBy(d => d.ObjectId)];
        node.Transport.OnAnnounce(peerA, new(descriptors));
        node.Transport.OnAnnounce(peerB, new(descriptors));
        GetProofWrappersMessage request = a.Sent<GetProofWrappersMessage>().Single();
        Assert.That(request.ObjectIds, Has.Length.EqualTo(3));
        byte[] Body(ValueHash256 id) => new[] { first, second, third }.Single(w => Describe(w).ObjectId == id);
        // The responder skipped the first wrapper and stopped after the second, as if at its soft limit.
        node.Transport.OnProofWrappers(peerA, new(request.RequestId, [Body(request.ObjectIds[1])]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Is.Empty);
            Assert.That(b.Sent<GetProofWrappersMessage>().Single().ObjectIds, Is.EqualTo(new[] { request.ObjectIds[0] }),
                "the skipped wrapper is unavailable from a, so b is asked");
            Assert.That(a.Sent<GetProofWrappersMessage>()[^1].ObjectIds, Is.EqualTo(new[] { request.ObjectIds[2] }),
                "the wrapper after the last one returned is requested again from a");
        }
    }

    [Test]
    public void Requests_stay_within_four_outstanding_and_the_soft_response_limit()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        WrapperDescriptor[] descriptors = [.. Enumerable.Range(0, 10)
            .Select(i => Describe(Wrapper(Eth73Protocol.SoftResponseLimit / 3, FrameTransaction(i)))).OrderBy(d => d.ObjectId)];
        node.Transport.OnAnnounce(peer, new(descriptors));
        GetProofWrappersMessage[] requests = link.Sent<GetProofWrappersMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Has.Length.EqualTo(Eth73Protocol.MaxRequestsPerPeer));
            Assert.That(requests.All(r => r.ObjectIds.Length == 2), Is.True, "two wrappers of a third of the soft limit fit, a third does not");
            Assert.That(node.Transport.ChargedBytes, Is.LessThanOrEqualTo(WrapperTransportLimits.MaxPeerBytes));
        }
    }

    [Test]
    public void Responder_returns_held_wrappers_in_request_order_and_always_the_first()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] first = Wrapper(Eip8288Constants.MaxAggregateProofBytes, a), second = Wrapper(Eip8288Constants.MaxAggregateProofBytes - 1, b);
        using WrapperTestNode node = Holder(first, a);
        node.Pending.Add(b);
        Assert.That(node.Transport.PublishWrapper(second), Is.True);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        ValueHash256 unknown = TestItem.KeccakA.ValueHash256;
        node.Transport.OnGetProofWrappers(peer, new(7, [unknown, Describe(second).ObjectId, Describe(first).ObjectId]));
        node.Transport.OnGetProofWrappers(peer, new(8, [unknown]));
        ProofWrappersMessage[] responses = link.Sent<ProofWrappersMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses[0].RequestId, Is.EqualTo(7));
            Assert.That(responses[0].Wrappers, Is.EqualTo(new[] { second }), "unknown IDs are skipped and the next one would pass the soft limit");
            Assert.That(responses[1].Wrappers, Is.Empty);
            Assert.That(new ProofWrappersMessageSerializer().Encode(responses[0]).Length, Is.LessThanOrEqualTo(Eth73Protocol.SoftResponseLimit));
        }
    }

    [Test]
    public void Envelope_requests_are_empty_for_unknown_wrappers_and_violations_past_the_transaction_count()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = Holder(wrapper, transaction);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnGetWrapperEnvelopes(peer, new(1, TestItem.KeccakA.ValueHash256, [5]));
        node.Transport.OnGetWrapperEnvelopes(peer, new(2, Describe(wrapper).ObjectId, [0]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Sent<WrapperEnvelopesMessage>()[0].Envelopes, Is.Empty);
            Assert.That(link.Sent<WrapperEnvelopesMessage>()[1].Envelopes, Is.EqualTo(new[] { Envelope(transaction) }));
            Assert.That(link.Penalties, Is.Empty);
        }
        node.Transport.OnGetWrapperEnvelopes(peer, new(3, Describe(wrapper).ObjectId, [1]));
        Assert.That(link.Penalties, Has.Length.EqualTo(1));
    }

    [TestCase("hash mismatch")]
    [TestCase("too many")]
    public async Task Envelope_hash_mismatch_or_excess_is_invalid_peer_data(string scenario)
    {
        Transaction transaction = FrameTransaction(1), other = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => link.Sent<GetWrapperEnvelopesMessage>().Length == 1);
        byte[][] envelopes = scenario == "hash mismatch" ? [Envelope(other)] : [Envelope(transaction), Envelope(other)];
        node.Transport.OnWrapperEnvelopes(peer, new(link.Sent<GetWrapperEnvelopesMessage>()[0].RequestId, envelopes));
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Is.Not.Empty);
            Assert.That(node.Pending, Is.Empty);
            Assert.That(node.Transport.IsStored(Describe(wrapper).ObjectId), Is.False);
        }
    }

    [Test]
    public async Task Empty_envelope_response_tries_another_announcer_without_penalty()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        WrapperDescriptor descriptor = Describe(wrapper);
        using WrapperTestNode node = new();
        FakeWrapperLink a = new("a"), b = new("b");
        WrapperPeer peerA = node.Connect(a), peerB = node.Connect(b);
        node.Transport.OnAnnounce(peerA, new([descriptor]));
        node.Transport.OnAnnounce(peerB, new([descriptor]));
        Answer(node, peerA, a, wrapper);
        await WrapperTestNode.Until(() => a.Sent<GetWrapperEnvelopesMessage>().Length == 1);
        node.Transport.OnWrapperEnvelopes(peerA, new(a.Sent<GetWrapperEnvelopesMessage>()[0].RequestId, []));
        await WrapperTestNode.Until(() => b.Sent<GetWrapperEnvelopesMessage>().Length == 1);
        node.Transport.OnWrapperEnvelopes(peerB, new(b.Sent<GetWrapperEnvelopesMessage>()[0].RequestId, [Envelope(transaction)]));
        await WrapperTestNode.Until(() => node.Transport.IsStored(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Is.Empty);
            Assert.That(b.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task Invalid_proof_is_tombstoned_and_its_server_penalized_before_any_envelope_request()
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
            Assert.That(link.Sent<GetWrapperEnvelopesMessage>(), Is.Empty);
            Assert.That(node.Transport.IsTombstoned(Describe(wrapper).ObjectId), Is.True);
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
        await WrapperTestNode.Until(() => !node.Transport.IsStored(Describe(mode1).ObjectId));
        node.Transport.OnGetProofWrappers(peer, new GetProofWrappersMessage(1, [Describe(mode1).ObjectId, Describe(mode0).ObjectId]));
        FakeWrapperLink late = new("late");
        node.Connect(late);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsOffered(Describe(mode0).ObjectId), Is.True);
            Assert.That(link.Sent<ProofWrappersMessage>().Single().Wrappers, Is.EqualTo(new[] { mode0 }), "the dropped wrapper is not served");
            Assert.That(late.Sent<AnnounceProofWrappersMessage>().Single().Descriptors.Select(d => d.ObjectId),
                Is.EqualTo(new[] { Describe(mode0).ObjectId }), "nor announced");
            Assert.That(node.Transport.PublishWrapper(mode1), Is.True, "re-validated under the new verifier, it is offered again");
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
            Assert.That(node.Transport.IsTombstoned(Describe(wrapper).ObjectId), Is.True);
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
        Assert.That(early.Sent<AnnounceProofWrappersMessage>(), Has.Length.EqualTo(WrapperTransportLimits.AnnouncementsPerSecond));
        node.Tick(TimeSpan.FromSeconds(1));
        AnnounceProofWrappersMessage[] announcements = early.Sent<AnnounceProofWrappersMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcements.Sum(a => a.Descriptors.Length), Is.EqualTo(6));
            Assert.That(announcements.SelectMany(a => a.Descriptors).Select(d => d.ObjectId).Distinct().Count(), Is.EqualTo(6));
        }
        FakeWrapperLink late = new("late");
        node.Connect(late);
        AnnounceProofWrappersMessage initial = late.Sent<AnnounceProofWrappersMessage>().Single();
        Assert.That(initial.Descriptors, Has.Length.EqualTo(6));
        Assert.That(initial.Descriptors.Select(d => d.ObjectId), Is.Ordered);
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
