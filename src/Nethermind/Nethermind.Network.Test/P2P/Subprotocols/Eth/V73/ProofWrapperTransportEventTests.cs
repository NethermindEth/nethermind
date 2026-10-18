// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.TxPool;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using T = Nethermind.Consensus.ProofAggregation.ProofWrapperEventTypes;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>The debug event feed records each EIP-8437 step the transport takes for a wrapper.</summary>
public class ProofWrapperTransportEventTests
{
    private static ProofWrapperEvent[] Events(ProofWrapperEventFeed feed, ValueHash256 wrapper) =>
        [.. feed.Read(0, 1_000).Events.Where(e => e.Wrapper?.ValueHash256 == wrapper)];

    private static ProofWrapperEvent Single(ProofWrapperEvent[] events, string type) => events.Single(e => e.Type == type);

    private static (long RequestId, ValueHash256[] Hashes) EnvelopeRequest(FakeWrapperLink link, int index = 0)
    {
        GetPooledTransactionsMessage66 request = link.Sent<GetPooledTransactionsMessage66>()[index];
        return (request.RequestId, [.. request.EthMessage.Hashes]);
    }

    private static void Answer(WrapperTestNode node, WrapperPeer peer, FakeWrapperLink link, params byte[][] wrappers) =>
        node.Transport.OnProofWrappers(peer, new ProofWrappersMessage(link.Sent<GetProofWrappersMessage>()[^1].RequestId, wrappers));

    [Test]
    public async Task Records_announcement_retrieval_recovery_validation_admission_and_relay_in_order()
    {
        ProofWrapperEventFeed holderEvents = new(256), nodeEvents = new(256);
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(1024, transaction);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode holder = new(events: holderEvents);
        holder.Pending.Add(transaction);
        Assert.That(holder.Transport.PublishWrapper(wrapper), Is.True);
        using WrapperTestNode node = new(events: nodeEvents);
        FakeWrapperLink link = new("source"), holderLink = new("requester"), onward = new("onward");
        WrapperPeer peer = node.Connect(link), requester = holder.Connect(holderLink);
        node.Connect(onward);

        node.Transport.OnAnnounce(peer, new([announcement]));
        holder.Transport.OnGetProofWrappers(requester, link.Sent<GetProofWrappersMessage>().Single());
        node.Transport.OnProofWrappers(peer, holderLink.Sent<ProofWrappersMessage>().Single());
        await WrapperTestNode.Until(() => link.Sent<GetPooledTransactionsMessage66>().Length == 1);
        (long requestId, _) = EnvelopeRequest(link);
        node.Transport.OnPooledEnvelopes(peer, requestId, [PooledItem(transaction)]);
        await WrapperTestNode.Until(() => Events(nodeEvents, announcement.Hash).Count(e => e.Type is T.Announced or T.Verdict) == 2);

        ProofWrapperEvent[] events = Events(nodeEvents, announcement.Hash);
        // Relay runs alongside pool admission once the wrapper is fully validated, so admission and the verdict are not ordered against it.
        string[] lifecycle = [T.AnnouncementReceived, T.WrapperRequested, T.WrapperReceived, T.ValidationStarted, T.EnvelopeRequested,
            T.EnvelopeResponse, T.EnvelopeRecovery, T.ProofVerified, T.Offered, T.Announced];
        ProofWrapperEvent request = Single(events, T.WrapperRequested);
        ProofWrapperEvent started = Single(events, T.ValidationStarted);
        ProofWrapperEvent envelopes = Single(events, T.EnvelopeRequested);
        ProofWrapperEvent served = Single(Events(holderEvents, announcement.Hash), T.Served);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.Select(e => e.Type).Where(lifecycle.Contains), Is.EqualTo(lifecycle));
            Assert.That(events.Select(e => e.Seq), Is.Ordered);
            Assert.That(Single(events, T.AnnouncementReceived).Result, Is.EqualTo("queued"));
            Assert.That(Single(events, T.AnnouncementReceived).Length, Is.EqualTo(announcement.Length));
            Assert.That(request.Message, Is.EqualTo("GetProofWrappers"));
            Assert.That(request.Peer, Is.EqualTo("source"));
            Assert.That(request.RequestId, Is.EqualTo(link.Sent<GetProofWrappersMessage>().Single().RequestId));
            Assert.That(started.Mode, Is.EqualTo(1));
            Assert.That(started.Count, Is.EqualTo(1), "one envelope to recover");
            Assert.That(started.Transactions, Is.EqualTo(new[] { transaction.Hash }));
            Assert.That(started.Dependencies, Has.Length.EqualTo(1));
            Assert.That(envelopes.Message, Is.EqualTo("GetPooledTransactions"));
            Assert.That(envelopes.RequestId, Is.EqualTo(requestId));
            Assert.That(envelopes.Transactions, Is.EqualTo(new[] { transaction.Hash }));
            Assert.That(Single(events, T.EnvelopeResponse).Result, Is.EqualTo("returned 1 of 1"));
            Assert.That(Single(events, T.EnvelopeRecovery).Result, Is.EqualTo("complete"));
            Assert.That(Single(events, T.ProofVerified).Result, Is.EqualTo("valid"));
            Assert.That(Single(events, T.Admitted).Result, Is.EqualTo(nameof(AcceptTxResult.Accepted)));
            Assert.That(Single(events, T.Verdict).Result, Is.EqualTo("Valid"));
            Assert.That(Single(events, T.Offered).Result, Is.EqualTo("validated"));
            Assert.That(Single(events, T.Announced).Peer, Is.EqualTo("onward"), "relayed only to the peer not known to hold it");
            Assert.That(Single(Events(holderEvents, announcement.Hash), T.Offered).Result, Is.EqualTo("local aggregate"));
            Assert.That(served.Result, Is.EqualTo("served"));
            Assert.That(served.Message, Is.EqualTo("ProofWrappers"));
        }
    }

    [Test]
    public async Task Records_omitted_envelopes_and_the_retry_from_another_announcer()
    {
        ProofWrapperEventFeed feed = new(256);
        Transaction first = FrameTransaction(1), second = FrameTransaction(2);
        byte[] wrapper = Wrapper(64, first, second);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new(events: feed);
        FakeWrapperLink a = new("a"), b = new("b");
        WrapperPeer peerA = node.Connect(a), peerB = node.Connect(b);
        node.Transport.OnAnnounce(peerA, new([announcement]));
        node.Transport.OnAnnounce(peerB, new([announcement]));
        Answer(node, peerA, a, wrapper);
        await WrapperTestNode.Until(() => a.Sent<GetPooledTransactionsMessage66>().Length == 1);
        (long requestId, ValueHash256[] requested) = EnvelopeRequest(a);
        Transaction Of(ValueHash256 hash) => hash == first.Hash!.ValueHash256 ? first : second;
        node.Transport.OnPooledEnvelopes(peerA, requestId, [PooledItem(Of(requested[1]))]);
        await WrapperTestNode.Until(() => b.Sent<GetPooledTransactionsMessage66>().Length == 1);
        node.Transport.OnPooledEnvelopes(peerB, EnvelopeRequest(b).RequestId, [PooledItem(Of(requested[0]))]);
        await WrapperTestNode.Until(() => node.Transport.IsStored(announcement.Hash));

        ProofWrapperEvent[] events = Events(feed, announcement.Hash);
        ProofWrapperEvent[] rounds = [.. events.Where(e => e.Type == T.EnvelopeRequested)];
        ProofWrapperEvent[] responses = [.. events.Where(e => e.Type == T.EnvelopeResponse)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.First(e => e.Type == T.AnnouncementReceived).Result, Is.EqualTo("queued"));
            Assert.That(events.Count(e => e.Type == T.AnnouncementReceived), Is.EqualTo(2));
            Assert.That(events.Last(e => e.Type == T.AnnouncementReceived).Result, Is.EqualTo("added as another source"));
            Assert.That(rounds.Select(e => e.Peer), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(rounds[1].Detail, Is.EqualTo("asking another announcer"));
            Assert.That(responses[0].Result, Is.EqualTo("returned 1 of 2"));
            Assert.That(responses[0].Detail, Does.StartWith("1 omitted before the last returned"));
            Assert.That(Single(events, T.EnvelopeRecovery).Count, Is.EqualTo(2), "two GetPooledTransactions rounds");
            Assert.That(Single(events, T.EnvelopeRecovery).Detail, Is.EqualTo("recovered 2 of 2 envelopes from 2 sources"));
        }
    }

    [Test]
    public async Task Records_an_invalid_verdict_and_the_disconnect_of_its_server()
    {
        ProofWrapperEventFeed feed = new(256);
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        WrapperAnnouncement announcement = Describe(wrapper);
        using WrapperTestNode node = new(events: feed);
        node.Verifier.Valid = false;
        FakeWrapperLink link = new("liar");
        WrapperPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([announcement]));
        Answer(node, peer, link, wrapper);
        await WrapperTestNode.Until(() => node.Transport.FetchCount == 0);

        ProofWrapperEvent[] all = feed.Read(0, 1_000).Events;
        ProofWrapperEvent verdict = Single(Events(feed, announcement.Hash), T.Verdict);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Result, Is.EqualTo("Invalid"));
            Assert.That(Single(Events(feed, announcement.Hash), T.ClaimsChecked).Result, Is.EqualTo("invalid"));
            Assert.That(all.Single(e => e.Type == T.Violation).Peer, Is.EqualTo("liar"));
            Assert.That(all.Single(e => e.Type == T.Violation).Result, Is.EqualTo("disconnected"));
            Assert.That(all.Select(e => e.Type), Does.Contain(T.PeerDisconnected));
            Assert.That(Events(feed, announcement.Hash).Select(e => e.Type), Has.No.Member(T.Offered));
        }
    }
}
