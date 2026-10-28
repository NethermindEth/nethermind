// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

public class ProofWrapperTransportTests
{
    /// <summary>Serves the last GetProofWrappers sent to <paramref name="link"/> with <paramref name="wrappers"/>, each under its
    /// canonical hash.</summary>
    private static void Answer(WrapperTestNode node, WrapperPeer peer, FakeWrapperLink link, params byte[][] wrappers) =>
        node.Transport.OnProofWrappers(peer, new ProofWrappersMessage(link.Sent<GetProofWrappersMessage>()[^1].RequestId,
            [.. wrappers.Select(Entry)]));

    /// <summary>Publishes a wrapper from a fresh node, and returns that node.</summary>
    private static WrapperTestNode Holder(byte[] wrapper)
    {
        WrapperTestNode holder = new();
        Assert.That(holder.Transport.PublishWrapper(wrapper), Is.True);
        return holder;
    }

    [Test]
    public async Task Announced_wrapper_is_fetched_complete_validated_admitted_and_offered()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(1024, transaction);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode holder = new();
        using WrapperTestNode node = new();
        FakeWrapperLink link = new("source");
        WrapperPeer peer = node.Connect(link);
        FakeWrapperLink holderLink = new("requester");
        WrapperPeer requester = holder.Connect(holderLink);
        Assert.That(holder.Transport.PublishWrapper(wrapper), Is.True);

        node.Transport.OnAnnounce(peer, holderLink.Sent<NewProofWrapperHashesMessage>().Single());
        GetProofWrappersMessage request = link.Sent<GetProofWrappersMessage>().Single();
        Assert.That(request.Hashes, Is.EqualTo(new[] { announcement.Hash }));
        Assert.That(request.Compact, Is.False, "nothing was exchanged in full on this connection yet");
        holder.Transport.OnGetProofWrappers(requester, request);
        ProofWrappersMessage response = holderLink.Sent<ProofWrappersMessage>().Single();
        Assert.That(response.Entries.Single().Hash, Is.EqualTo(announcement.Hash));
        node.Transport.OnProofWrappers(peer, response);

        await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash) && node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pending.Select(t => t.Hash), Is.EqualTo(new[] { transaction.Hash }), "the wrapper carries its transaction");
            Assert.That(holder.Pending.Select(t => t.Hash), Is.Empty, "the holder served the wrapper without pooling its transactions");
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(holderLink.Penalties, Is.Empty);
            Assert.That(node.Transport.ChargedBytes, Is.Zero, "every reservation is released once validation finishes");
            Assert.That(link.Sent<NewProofWrapperHashesMessage>(), Is.Empty, "the source already holds the wrapper");
        }
    }

    [Test]
    public void Wrapper_matching_no_requested_hash_is_a_violation()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        byte[] wanted = Wrapper(64, FrameTransaction(1)), other = Wrapper(64, FrameTransaction(2));
        node.Transport.OnAnnounce(peer, new([Describe(wanted)]));
        Answer(node, peer, link, other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties.Single(), Does.Contain("unrequested, repeated or reordered"));
            Assert.That(node.Transport.PeerCount, Is.Zero);
            Assert.That(node.Transport.ChargedBytes, Is.Zero);
        }
    }

    // EIP-8437: requesters check each returned wrapper's canonical hash and the responder's announced mode and length.
    // A hash reference in a compact = 0 response is rejected too.
    [TestCase("hash")]
    [TestCase("mode")]
    [TestCase("length")]
    [TestCase("reference")]
    [TestCase("repeat")]
    public void Mismatched_entries_are_violations(string scenario)
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, a, b);
        WrapperAnnouncement truth = Describe(wrapper);
        WrapperAnnouncement announced = scenario switch
        {
            "mode" => WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)(1 - truth.Mode)),
                Eth73Rlp.EncodeUInt((ulong)truth.Length), Eth73Rlp.EncodeBytes(truth.Hash.Bytes))),
            "length" => WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)truth.Mode),
                Eth73Rlp.EncodeUInt((ulong)truth.Length + 1), Eth73Rlp.EncodeBytes(truth.Hash.Bytes))),
            _ => truth
        };
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([announced]));
        long requestId = link.Sent<GetProofWrappersMessage>().Single().RequestId;
        ProofWrapperEntry[] entries = scenario switch
        {
            "hash" => [new(truth.Hash, Wrapper(64, 7, a, b))],
            "reference" => [new(truth.Hash, Compact(wrapper))],
            "repeat" => [Entry(wrapper), Entry(wrapper)],
            _ => [Entry(wrapper)]
        };
        node.Transport.OnProofWrappers(peer, new(requestId, entries));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Has.Length.EqualTo(1));
            if (scenario == "reference") Assert.That(link.Penalties[0], Does.Contain("compact = 0"));
            Assert.That(node.Transport.ChargedBytes, Is.Zero);
            Assert.That(node.Pending, Is.Empty);
        }
    }

    /// <summary>The wrapper with each transaction replaced by its <c>[encoding-hash]</c> reference.</summary>
    private static byte[] Compact(byte[] wrapper)
    {
        Eth73RlpReader outer = new(wrapper);
        Eth73RlpReader fields = outer.ReadList();
        Eth73RlpReader transactions = fields.ReadList();
        System.Collections.Generic.List<byte[]> references = [];
        while (transactions.HasMore) references.Add(Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(ValueKeccak.Compute(transactions.ReadBytes()).Bytes)));
        byte[] mode = fields.ReadItem().ToArray();
        byte[] content = fields.ReadItem().ToArray();
        return Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. references]), mode, content);
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
        node.Transport.OnProofWrappers(peerA, new(request.RequestId, [Entry(Body(request.Hashes[1]))]));
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
        node.Transport.OnProofWrappers(honestPeer, new(honest.Sent<GetProofWrappersMessage>().Single().RequestId, [Entry(wrapper)]));

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

    // EIP-8437: a responder adds no wrapper once the response reaches the soft limit, so the last one added may take it past
    // the limit; check_vectors.py packs three of five 1 MB wrappers. Hashes it has not announced on the connection are
    // unavailable.
    [Test]
    public void Responder_returns_announced_wrappers_in_request_order_until_the_soft_limit_is_reached()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2), c = FrameTransaction(3);
        byte[] first = Wrapper(Eip8288Constants.MaxAggregateProofBytes, a), second = Wrapper(Eip8288Constants.MaxAggregateProofBytes - 1, b),
            third = Wrapper(Eip8288Constants.MaxAggregateProofBytes - 2, c);
        using WrapperTestNode node = Holder(first);
        Assert.That(node.Transport.PublishWrapper(second), Is.True);
        Assert.That(node.Transport.PublishWrapper(third), Is.True);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        ValueHash256 unknown = TestItem.KeccakA.ValueHash256;
        node.Transport.OnGetProofWrappers(peer, new(7, false, [unknown, Describe(second).Hash, Describe(first).Hash, Describe(third).Hash]));
        node.Transport.OnGetProofWrappers(peer, new(8, true, [unknown]));
        ProofWrappersMessage[] responses = link.Sent<ProofWrappersMessage>();
        int length = new ProofWrappersMessageSerializer().Encode(responses[0]).Length;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses[0].RequestId, Is.EqualTo(7));
            Assert.That(responses[0].Entries.Select(static e => e.Wrapper), Is.EqualTo(new[] { second, first }),
                "unknown IDs are skipped, the second wrapper overshoots the soft limit and the third is not added");
            Assert.That(responses[0].Entries.Select(static e => e.Hash), Is.EqualTo(new[] { Describe(second).Hash, Describe(first).Hash }));
            Assert.That(responses[1].Entries, Is.Empty);
            Assert.That(length, Is.GreaterThan(Eth73Protocol.SoftResponseLimit).And.LessThanOrEqualTo(Eth73Protocol.LocalMaxResponseBytes));
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, length), Is.False, "overshooting the soft limit is valid");
        }
    }

    // EIP-8437: responders treat hashes they have not announced on the connection as unavailable, including held wrappers
    // still queued behind the announcement rate limit and hashes the peer announced to them.
    [Test]
    public void Wrappers_not_announced_on_the_connection_are_unavailable()
    {
        Transaction a = FrameTransaction(1), b = FrameTransaction(2), c = FrameTransaction(3);
        byte[] announced = Wrapper(64, a), queued = Wrapper(64, b), peerOwn = Wrapper(64, c);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        Assert.That(node.Transport.PublishWrapper(announced), Is.True);
        for (int i = 1; i < WrapperTransportLimits.AnnouncementsPerSecond; i++) Assert.That(node.Transport.PublishWrapper(Wrapper(64, 10 + i, a)), Is.True);
        Assert.That(node.Transport.PublishWrapper(queued), Is.True);
        Assert.That(node.Transport.PublishWrapper(peerOwn), Is.True);
        node.Transport.OnAnnounce(peer, new([Describe(peerOwn)]));

        node.Transport.OnGetProofWrappers(peer, new(9, true, [Describe(announced).Hash, Describe(queued).Hash, Describe(peerOwn).Hash]));

        Assert.That(link.Sent<ProofWrappersMessage>().Single().Entries.Select(static e => e.Wrapper), Is.EqualTo(new[] { announced }));
    }

    // EIP-8437: a proof failure discards the wrapper without penalizing the peer that served it.
    [Test]
    public async Task Invalid_proof_is_tombstoned_without_penalty()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = new();
        node.Verifier.Valid = false;
        FakeWrapperLink link = new(), other = new("other");
        WrapperPeer peer = node.Connect(link);
        node.Connect(other);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(node.Transport.PeerCount, Is.EqualTo(2));
            Assert.That(node.Pending, Is.Empty, "no transaction is admitted under an invalid proof");
            Assert.That(node.Transport.IsTombstoned(Describe(wrapper).Hash), Is.True);
            Assert.That(link.Sent<GetProofWrappersMessage>(), Has.Length.EqualTo(1), "a discarded wrapper is not fetched again");
            Assert.That(other.Sent<NewProofWrapperHashesMessage>(), Is.Empty, "never relayed");
        }
    }

    // EIP-8437: malformed transport encodings remain penalizable, unlike proof failures.
    [TestCase("duplicate transaction")]
    [TestCase("transaction without frames")]
    public async Task Malformed_wrapper_is_tombstoned_and_its_server_penalized(string scenario)
    {
        Transaction transaction = FrameTransaction(1);
        byte[] valid = Wrapper(64, transaction);
        Eth73RlpReader outer = new(valid);
        Eth73RlpReader fields = outer.ReadList();
        byte[] envelope = fields.ReadList().ReadBytes().ToArray();
        byte[] rest = [.. fields.ReadItem(), .. fields.ReadItem()];
        byte[] wrapper = scenario == "duplicate transaction"
            ? Eth73Rlp.EncodeList(Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(envelope), Eth73Rlp.EncodeBytes(envelope)), rest)
            : Eth73Rlp.EncodeList(Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([(byte)TxType.FrameTx, 0xc1, 0x01])), rest);
        using WrapperTestNode node = new();
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties.Single(), Does.Contain("malformed"));
            Assert.That(node.Pending, Is.Empty);
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
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);

        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(node.Transport.IsStored(Describe(wrapper).Hash), Is.False);
            Assert.That(link.Sent<GetProofWrappersMessage>(), Has.Length.EqualTo(1), "the re-announcement is not fetched again");
        }
    }

    // EIP-8437: a validation cancelled at the fetch deadline releases its request-scoped buffer without admitting anything.
    [Test]
    public async Task Timed_out_validation_releases_its_buffer_without_admission()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using WrapperTestNode node = new();
        using System.Threading.ManualResetEventSlim hold = new();
        node.Verifier.Hold = hold;
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Verifier.Calls == 1);
        // Charged like transaction gossip: the larger of its bytes and a per-transaction overhead.
        Assert.That(node.Transport.ChargedBytes, Is.EqualTo(Math.Max(wrapper.Length, InboundTransactionBudget.TransactionCharge)));

        node.Tick(WrapperTransportLimits.FetchDeadline + TimeSpan.FromSeconds(1));
        hold.Set();
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.ChargedBytes, Is.Zero);
            Assert.That(node.Pending, Is.Empty);
            Assert.That(node.Transport.IsStored(Describe(wrapper).Hash), Is.False);
            Assert.That(link.Penalties, Is.Empty);
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

    // EIP-8437 / local verifier-change rule: once the target fork's verifier changes, every mode-1 wrapper is dropped and never
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
        Assert.That(node.Transport.PublishWrapper(mode1), Is.True);
        Assert.That(node.Transport.PublishWrapper(mode0), Is.True);
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);

        CrossVerifierFork(node);
        await WrapperTestNode.Until(() => !node.Transport.IsStored(Describe(mode1).Hash));
        node.Transport.OnGetProofWrappers(peer, new GetProofWrappersMessage(1, false, [Describe(mode1).Hash, Describe(mode0).Hash]));
        FakeWrapperLink late = new("late");
        node.Connect(late);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsOffered(Describe(mode0).Hash), Is.True);
            Assert.That(link.Sent<ProofWrappersMessage>().Single().Entries.Select(static e => e.Wrapper), Is.EqualTo(new[] { mode0 }),
                "the dropped wrapper is not served");
            Assert.That(late.Sent<NewProofWrapperHashesMessage>().Single().Announcements.Select(d => d.Hash),
                Is.EqualTo(new[] { Describe(mode0).Hash }), "nor announced");
            // Suppression records reset at the verifier change, so the revalidated wrapper is announced again on the same
            // connection.
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
            Assert.That(changes, Is.EqualTo(2), "the faulted verifier-change rule runs again on the next tick");
            Assert.That(node.Wrappers.CurrentVerifier, Is.EqualTo(new Eip8288Verifier(UpgradedVk)));
        }
    }

    // EIP-8437: proof failures never penalize, including across a verifier change; there is no grace window to configure.
    [TestCase(11UL)]
    [TestCase(200UL)]
    public async Task Proof_failures_across_a_verifier_change_are_not_penalized(ulong headNumber)
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
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(node.Transport.IsTombstoned(Describe(wrapper).Hash), Is.True);
        }
    }

    // A wrapper invalid under one AGGREGATED_VK may be valid under the next, so invalid-proof tombstones end at a verifier
    // change while malformed-encoding ones remain.
    [Test]
    public async Task Verifier_change_lifts_invalid_tombstones_but_keeps_malformed_ones()
    {
        using WrapperTestNode node = new(UpgradingChain());
        node.SetHead(10, 100);
        node.Wrappers.RefreshVerifier();
        node.Verifier.Valid = false;
        byte[] invalid = Wrapper(64, FrameTransaction(1));
        Eth73RlpReader fields = new Eth73RlpReader(Wrapper(64, FrameTransaction(2))).ReadList();
        fields.ReadItem();
        byte[] malformed = Eth73Rlp.EncodeList(Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([(byte)TxType.FrameTx, 0xc1, 0x01])),
            [.. fields.ReadItem(), .. fields.ReadItem()]);
        FakeWrapperLink link = new(), bad = new("bad");
        WrapperPeer peer = node.Connect(link), badPeer = node.Connect(bad);
        node.Transport.OnAnnounce(peer, new([Describe(invalid)]));
        Answer(node, peer, link, invalid);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        node.Transport.OnAnnounce(badPeer, new([Describe(malformed)]));
        Answer(node, badPeer, bad, malformed);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        Assert.That(node.Transport.IsTombstoned(Describe(invalid).Hash), Is.True);

        CrossVerifierFork(node);
        node.Verifier.Valid = true;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsTombstoned(Describe(invalid).Hash), Is.False);
            Assert.That(node.Transport.IsTombstoned(Describe(malformed).Hash), Is.True);
        }
        node.Transport.OnAnnounce(peer, new([Describe(invalid)]));
        Answer(node, peer, link, invalid);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        Assert.That(node.Transport.IsStored(Describe(invalid).Hash), Is.True, "fetched and accepted under the new verifier");
    }

    // An invalid verdict is remembered under the verifier the service judged it with, even when validation itself applied the
    // verifier change the transport had not yet seen.
    [Test]
    public async Task Invalid_verdict_is_scoped_to_the_verifier_validation_used()
    {
        using WrapperTestNode node = new(UpgradingChain());
        node.SetHead(10, 100);
        node.Wrappers.RefreshVerifier();
        node.Verifier.Valid = false;
        byte[] wrapper = Wrapper(64, FrameTransaction(1));
        FakeWrapperLink link = new();
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));
        node.SetHead(10, 990);
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);
        node.Transport.OnAnnounce(peer, new([Describe(wrapper)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Wrappers.CurrentVerifier, Is.EqualTo(new Eip8288Verifier(UpgradedVk)), "validation applied the change");
            Assert.That(node.Transport.IsTombstoned(Describe(wrapper).Hash), Is.True);
            Assert.That(link.Sent<GetProofWrappersMessage>(), Has.Length.EqualTo(1), "not fetched again under the same verifier");
        }
    }

    // EIP-8437: until EIP-8288 is enabled for local mempool validation, no wrapper message is sent; a chain without a
    // scheduled EIP-8288 fork registers no peers at all.
    [Test]
    public void No_wrapper_message_is_sent_while_eip8288_is_inactive()
    {
        using WrapperTestNode node = new();
        node.Tree.Head.Returns((Block?)null);
        node.Tick(WrapperTransportLimits.Tick);
        FakeWrapperLink link = new();
        WrapperPeer? peer = node.Transport.Accept(link);
        using WrapperTestNode unscheduled = new(new TestSingleReleaseSpecProvider(Osaka.Instance));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer, Is.Not.Null);
            Assert.That(node.Transport.PublishWrapper(Wrapper(64, FrameTransaction(1))), Is.False);
            Assert.That(link.Sent<NewProofWrapperHashesMessage>(), Is.Empty);
            Assert.That(unscheduled.Transport.Accept(new FakeWrapperLink()), Is.Null);
        }
    }

    [Test]
    public void Published_wrappers_are_announced_once_within_the_rate_limit_and_to_new_peers()
    {
        using WrapperTestNode node = new();
        FakeWrapperLink early = new("early");
        node.Connect(early);
        Transaction[] transactions = [.. Enumerable.Range(0, 6).Select(FrameTransaction)];
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
    public void Publishing_requires_a_canonical_wrapper()
    {
        using WrapperTestNode node = new();
        byte[] wrapper = Wrapper(64, FrameTransaction(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.PublishWrapper([.. wrapper, 0x80]), Is.False);
            Assert.That(node.Transport.PublishWrapper(wrapper), Is.True);
        }
    }
}
