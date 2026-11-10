// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Threading;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public partial class GossipRouterTests
{
    [TestCase(0UL, false)]
    [TestCase(4096UL, true)]
    public void The_pending_byte_backstop_starts_the_host_for_any_blob_schedule(ulong maxBlobsPerBlock, bool saturated)
    {
        BeaconChainSpec spec = Spec with { BlobSchedule = [.. Spec.BlobSchedule, new BlobScheduleEntry(ulong.MaxValue, maxBlobsPerBlock)] };

        int backstop = BeaconP2P.PendingValidationBytesBackstop(new BeaconChainConfig(), spec);

        Assert.That(backstop, saturated ? Is.EqualTo(int.MaxValue) : Is.LessThan(int.MaxValue), "a backstop past int range must saturate, not fail the host's startup");
    }

    // phase0 p2p-interface.md: REJECT and IGNORE are not forwarded.
    [Test]
    public async Task Deferred_block_is_sent_to_the_mesh_only_once_its_verdict_is_accepted(
        [Values(MessageValidity.Accepted, MessageValidity.Rejected, MessageValidity.Ignored)] MessageValidity validity)
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 8, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        byte[] block = BlockMessage(CurrentSlot);

        fixture.Receive(fixture.Sender, block);
        bool forwardedWhilePending = fixture.SentToNeighbor(block);
        int pending = fixture.Pubsub.PendingValidationCount;
        bool applied = fixture.Raised.Single().Complete(validity);
        fixture.Receive(fixture.Neighbor, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((forwardedWhilePending, pending), Is.EqualTo((false, 1)), "the router holds a deferred message");
        Assert.That(applied, Is.True);
        Assert.That(rejected, Is.EqualTo(validity == MessageValidity.Rejected ? new[] { fixture.Sender } : Array.Empty<PeerId>()));
        Assert.That(fixture.SentToNeighbor(block), Is.EqualTo(validity == MessageValidity.Accepted), "only an accepted message is sent on");
        Assert.That(fixture.Router.GetDropCount(GossipDropReason.Duplicate), Is.Zero, "the router caches the id of a message with a verdict, so a copy is not validated again");
        Assert.That((fixture.Pubsub.PendingValidationCount, fixture.Validation.PendingBytes), Is.EqualTo((0, 0L)), "a given verdict releases its message");
    }

    // phase0 p2p-interface.md: queue bounds SHOULD prevent DoS.
    [Test]
    public async Task Message_past_the_pending_bound_is_throttled_and_validated_once_the_bound_frees([Values] bool byBytes)
    {
        byte[] first = BlockMessage(CurrentSlot);
        byte[] second = BlockMessage(CurrentSlot - 1);
        int firstSize = new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(first) }.CalculateSize();
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: byBytes ? 64 : 1, maxPendingBytes: byBytes ? firstSize + 1 : 1 << 20, TimeSpan.FromSeconds(30));
        ulong throttledBefore = Metrics.BeaconChainGossipThrottled;

        fixture.Receive(fixture.Sender, first);
        fixture.Receive(fixture.Sender, second);
        int raisedWhileFull = fixture.Raised.Count;
        fixture.Raised[0].Complete(MessageValidity.Ignored);
        fixture.Receive(fixture.Sender, second);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(raisedWhileFull, Is.EqualTo(1), "the message past the bound is not validated");
        Assert.That(Metrics.BeaconChainGossipThrottled - throttledBefore, Is.EqualTo(1UL), "the throttled message is counted");
        Assert.That(fixture.Raised, Has.Count.EqualTo(2), "the throttled message is validated once the bound frees");
    }

    [Test]
    public async Task One_peer_holds_at_most_its_share_of_the_pending_messages()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        int share = fixture.Validation.MaxPendingPerSource;

        // One RPC: the router checks all its messages before it dispatches any of them.
        Rpc rpc = new();
        for (int i = 0; i <= share; i++)
        {
            rpc.Publish.Add(new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - (ulong)i)) });
        }

        fixture.ReceiveRpc(fixture.Sender, rpc);

        int raisedFromSender = fixture.Raised.Count;
        fixture.Receive(fixture.Neighbor, BlockMessage(CurrentSlot - (ulong)share - 1));
        Message vote = new() { Topic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof), Data = ByteString.CopyFrom([1]) };

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((share, raisedFromSender), Is.EqualTo((2, 2)), "the message past the peer's share is throttled");
        Assert.That(fixture.Raised, Has.Count.EqualTo(3), "another peer's message is still validated");
        Assert.That(fixture.Validation.Verify(fixture.Sender, vote), Is.EqualTo(MessageValidity.Deferred), "votes have a separate per-peer share");
    }

    [Test]
    public async Task One_rpc_of_votes_or_columns_from_one_peer_is_held_to_its_share([Values] bool votes)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 128, maxPendingBytes: 1 << 24, TimeSpan.FromSeconds(30), maxPendingVotes: 128);
        byte[] digest = ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot));
        string Topic(int index) => GossipTopics.Topic(digest, votes ? GossipTopics.BeaconAggregateAndProof : GossipTopics.DataColumnSidecarTopicName((ulong)index));
        MessageValidity[] messages = [.. Enumerable.Range(0, 128).Select(subnet => fixture.Validation.Verify(fixture.Sender,
            new Message { Topic = Topic(subnet), Data = ByteString.CopyFrom([(byte)subnet]) }))];

        MessageValidity block = fixture.Validation.Verify(fixture.Sender, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        int share = votes ? fixture.Validation.MaxPendingVotesPerNewSource : fixture.Validation.MaxPendingPerSource;
        Assert.That(share, Is.LessThan(messages.Length), "fixture: more messages than the peer's share");
        Assert.That(messages.Take(share), Is.All.EqualTo(MessageValidity.Deferred));
        Assert.That(messages.Skip(share), Is.All.EqualTo(MessageValidity.Throttled));
        Assert.That(fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = Topic(0), Data = ByteString.CopyFrom([0]) }), Is.EqualTo(MessageValidity.Deferred));
        Assert.That(block, Is.EqualTo(MessageValidity.Deferred));
        Assert.That(fixture.Validation.Pending, Is.EqualTo(1), "votes and columns do not consume block reservations");
    }

    // A peer id costs nothing, so a share per peer alone lets one host take the whole vote queue with enough ids.
    [Test]
    public async Task Peers_with_no_accepted_vote_share_one_bound_however_many_ids_connect(
        [Values(MessageValidity.Accepted, MessageValidity.Ignored, MessageValidity.Rejected)] MessageValidity earlierVote)
    {
        const int maxPendingVotes = 1024;
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), maxPendingVotes: maxPendingVotes);
        await fixture.CompleteVoteFromAsync(fixture.Neighbor, aggregatorIndex: 1, earlierVote);
        Message Vote() => new() { Topic = DeferredFixture.AggregateTopic, Data = ByteString.CopyFrom([1]) };
        int Admitted(PeerId peer) => Enumerable.Range(0, maxPendingVotes / 8).Count(_ => fixture.Validation.Verify(peer, Vote()) == MessageValidity.Deferred);

        int newIds = fixture.Validation.MaxPendingVotesOfAllNewSources / fixture.Validation.MaxPendingVotesPerNewSource + 8;
        int[] admittedPerNewId = [.. Enumerable.Range(0, newIds).Select(static _ => NewPeerId()).Select(Admitted)];
        int admittedFromEarlierPeer = Admitted(fixture.Neighbor);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admittedPerNewId.Max(), Is.EqualTo(fixture.Validation.MaxPendingVotesPerNewSource).And.EqualTo(2), "a new id gets two votes in flight");
        Assert.That(admittedPerNewId.Sum(), Is.EqualTo(fixture.Validation.MaxPendingVotesOfAllNewSources).And.EqualTo(256), "new ids together get a quarter of the vote queue");
        Assert.That(fixture.Validation.PendingVotes, Is.EqualTo(fixture.Validation.MaxPendingVotesOfAllNewSources + admittedFromEarlierPeer));
        Assert.That(admittedFromEarlierPeer, Is.EqualTo(earlierVote == MessageValidity.Accepted ? 128 : 0),
            "a peer with an accepted vote keeps its full share; one whose vote was not accepted is still new");
    }

    // As the payload attestation rule, for votes from peers with no accepted vote, but at admission: a refused vote takes no room and leaves the
    // router no id, so another peer's copy is checked, and nothing unchecked counts against the peer.
    [Test]
    public async Task Votes_after_a_rejected_one_from_a_new_peer_are_throttled_at_admission_for_the_slot_uncharged()
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        (GossipVerdict? first, Task firstValidation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 1);
        (GossipVerdict? queued, Task queuedValidation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 2);
        first!.Complete(MessageValidity.Rejected);
        await firstValidation.WaitAsync(TimeSpan.FromSeconds(5));
        bool queuedCompletedByRejection = queued!.IsCompleted;
        queued.Complete(MessageValidity.Ignored);
        await queuedValidation.WaitAsync(TimeSpan.FromSeconds(5));
        (ulong throttledBefore, ulong refusedBefore) = (Metrics.BeaconChainGossipThrottled, Metrics.BeaconChainGossipVotesRefusedAfterRejection);

        MessageValidity[] later = [.. Enumerable.Range(3, 10).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];
        int pendingAfterRefusals = fixture.Validation.PendingVotes;
        (GossipVerdict? copy, _) = fixture.SendVote(fixture.Neighbor, aggregatorIndex: 3);
        copy?.Complete(MessageValidity.Accepted);
        fixture.Timestamper.UtcNow = fixture.Timestamper.UtcNow.AddSeconds(Spec.SecondsPerSlot);
        (GossipVerdict? nextSlot, _) = fixture.SendVote(fixture.Sender, aggregatorIndex: 20);
        nextSlot?.Complete(MessageValidity.Ignored);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(queuedCompletedByRejection, Is.False, "a vote deferred before the rejection is still checked");
        Assert.That(later, Is.All.EqualTo(MessageValidity.Throttled), "a later vote is refused at admission");
        Assert.That(Metrics.BeaconChainGossipVotesRefusedAfterRejection - refusedBefore, Is.EqualTo((ulong)later.Length), "each refusal is counted as one");
        Assert.That(Metrics.BeaconChainGossipThrottled - throttledBefore, Is.Zero, "a refusal is not counted as a throttle for load");
        Assert.That(pendingAfterRefusals, Is.Zero, "a refused vote takes no room");
        Assert.That(copy, Is.Not.Null, "another peer's copy of a refused vote is checked");
        Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender }), "only the rejected vote counts against the peer");
        Assert.That(nextSlot, Is.Not.Null, "the peer's votes are admitted again in the next slot");
    }

    // One relayed valid vote must not keep an id's forgeries admitted: its first rejected vote demotes it to a new peer's share and refuses its
    // votes for the rest of the slot. Its votes already deferred are still checked.
    [Test]
    public async Task Peer_promoted_by_one_valid_vote_is_demoted_and_refused_for_the_slot_at_its_first_rejected_vote()
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Accepted);
        (GossipVerdict? Raised, Task Validation)[] forgeries = [.. Enumerable.Range(2, 4).Select(index => fixture.SendVote(fixture.Sender, (ulong)index))];
        int inFlightWhilePromoted = fixture.Validation.PendingVotes;
        forgeries[0].Raised!.Complete(MessageValidity.Rejected);
        await forgeries[0].Validation.WaitAsync(TimeSpan.FromSeconds(5));
        ulong refusedBefore = Metrics.BeaconChainGossipVotesRefusedAfterRejection;

        MessageValidity[] sameSlot = [.. Enumerable.Range(10, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];
        ulong refusedForSlot = Metrics.BeaconChainGossipVotesRefusedAfterRejection - refusedBefore;
        bool queuedCompletedByRejection = forgeries[1..].Any(static forgery => forgery.Raised!.IsCompleted);
        foreach ((GossipVerdict? raised, Task validation) in forgeries[1..])
        {
            raised!.Complete(MessageValidity.Rejected);
            await validation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        fixture.Timestamper.UtcNow = fixture.Timestamper.UtcNow.AddSeconds(Spec.SecondsPerSlot);
        MessageValidity[] nextSlot = [.. Enumerable.Range(20, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(inFlightWhilePromoted, Is.EqualTo(4), "fixture: the promoted peer has more votes in flight than a new peer may");
        Assert.That(sameSlot, Is.All.EqualTo(MessageValidity.Throttled), "after its first rejected vote the peer's votes are refused for the slot");
        Assert.That(refusedForSlot, Is.EqualTo((ulong)sameSlot.Length), "each is refused at admission, not throttled by a new peer's share");
        Assert.That(queuedCompletedByRejection, Is.False, "its votes deferred before the rejection are still checked");
        Assert.That(nextSlot, Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Deferred, MessageValidity.Throttled }), "in the next slot it is held to a new peer's two votes");
        Assert.That(rejected, Has.Count.EqualTo(4).And.All.EqualTo(fixture.Sender));
    }

    // An honest peer whose view differs from ours (a vote for a block we failed to import, a committee from a head we do not share) can have
    // a vote rejected: it is demoted, and its next accepted vote makes it a peer with an accepted vote again, in the same slot (a vote deferred
    // before the rejection) or a later one.
    [Test]
    public async Task Demoted_peer_is_promoted_again_by_its_next_accepted_vote([Values] bool sameSlot)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Accepted);
        (GossipVerdict? valid, Task validValidation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 2);
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 3, MessageValidity.Rejected);
        MessageValidity whileDemoted = fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(4));
        if (sameSlot)
        {
            valid!.Complete(MessageValidity.Accepted);
            await validValidation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            valid!.Complete(MessageValidity.Ignored);
            await validValidation.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Timestamper.UtcNow = fixture.Timestamper.UtcNow.AddSeconds(Spec.SecondsPerSlot);
            await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 5, MessageValidity.Accepted);
        }

        MessageValidity[] votes = [.. Enumerable.Range(10, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(whileDemoted, Is.EqualTo(MessageValidity.Throttled), "fixture: the rejected vote demoted the peer and refused its votes");
        Assert.That(votes, Is.All.EqualTo(MessageValidity.Deferred), "the accepted vote gives the peer back its share, past a new peer's two votes");
    }

    // The router dispatches a peer's later message without waiting for the verdicts of its earlier ones, so a vote can be rejected while an
    // earlier valid one of the same peer is being checked in the import worker; that acceptance must lift the refusal.
    [Test]
    public async Task Rejection_refuses_a_new_peers_votes_at_admission_until_its_vote_being_checked_is_accepted()
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        (GossipVerdict? valid, Task validValidation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 1);
        Message malformed = new() { Topic = DeferredFixture.AggregateTopic, Data = ByteString.CopyFrom([1]) };
        Assert.That(fixture.Validation.Verify(fixture.Sender, malformed), Is.EqualTo(MessageValidity.Deferred), "fixture: the malformed vote is reserved");
        await fixture.Validation.ValidateAsync(fixture.Sender, malformed);
        int rejectedBeforeAcceptance = rejected.Count;
        MessageValidity whileMarked = fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(2));
        valid!.Complete(MessageValidity.Accepted);
        await validValidation.WaitAsync(TimeSpan.FromSeconds(5));

        (GossipVerdict? next, _) = fixture.SendVote(fixture.Sender, aggregatorIndex: 2);
        next?.Complete(MessageValidity.Ignored);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(rejectedBeforeAcceptance, Is.EqualTo(1), "fixture: the malformed vote is rejected while the valid one is being checked");
        Assert.That(whileMarked, Is.EqualTo(MessageValidity.Throttled), "until then the peer's new votes are refused at admission");
        Assert.That(next, Is.Not.Null, "after the acceptance the next vote is admitted and handed to the import worker");
        Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender }), "only the malformed vote counts against the peer");
    }

    // The accepted-peer set is bounded, so a peer can be evicted from it within the slot in which its acceptance lifted a refusal; it is then
    // new again, and only clearing the mark on that acceptance keeps its votes admitted.
    [Test]
    public async Task Peer_evicted_after_its_accepted_vote_lifted_a_refusal_is_admitted_again_in_the_slot()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        (GossipVerdict? valid, Task validValidation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 1);
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 2, MessageValidity.Rejected);
        MessageValidity whileMarked = fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(3));
        valid!.Complete(MessageValidity.Accepted);
        await validValidation.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.EvictFromAcceptedPeersAsync();

        MessageValidity[] votes = [.. Enumerable.Range(3, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(whileMarked, Is.EqualTo(MessageValidity.Throttled), "fixture: the rejection refused the peer's votes");
        Assert.That(votes[0], Is.EqualTo(MessageValidity.Deferred), "the acceptance cleared the refusal, so the evicted peer's next vote is admitted");
        Assert.That(votes[1..], Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Throttled }), "fixture: the peer was evicted, so it is held to a new peer's two votes");
    }

    // The accepted-peer set is bounded: a rejected vote from a peer it no longer holds marks that peer as a new one and demotes no other peer.
    [Test]
    public async Task Rejected_vote_from_a_peer_evicted_from_the_accepted_set_demotes_no_other_peer()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Accepted);
        PeerId[] evicting = await fixture.EvictFromAcceptedPeersAsync();
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 2, MessageValidity.Rejected);

        MessageValidity refused = fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(3));
        MessageValidity[] oldest = [.. Enumerable.Range(10, 3).Select(index => fixture.Validation.Verify(evicting[0], DeferredFixture.Vote((ulong)index)))];
        MessageValidity[] newest = [.. Enumerable.Range(20, 3).Select(index => fixture.Validation.Verify(evicting[^1], DeferredFixture.Vote((ulong)index)))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refused, Is.EqualTo(MessageValidity.Throttled), "the evicted peer is refused for the slot, as a new peer is");
        Assert.That(oldest, Is.All.EqualTo(MessageValidity.Deferred), "the oldest peer still held keeps its share");
        Assert.That(newest, Is.All.EqualTo(MessageValidity.Deferred), "the newest peer keeps its share");
    }

    // A vote whose reservation expired before the router dispatched it is validated unreserved; it is still a vote, so its rejection marks its peer.
    [Test]
    public async Task Rejected_vote_whose_reservation_expired_before_dispatch_refuses_its_peers_votes()
    {
        ManualTimeProvider time = new();
        time.JumpUtc(TimeSpan.FromDays(20_000));
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), time: time);
        Message malformed = new() { Topic = DeferredFixture.AggregateTopic, Data = ByteString.CopyFrom([1]) };
        Assert.That(fixture.Validation.Verify(fixture.Sender, malformed), Is.EqualTo(MessageValidity.Deferred), "fixture: the malformed vote is reserved");
        time.Advance(TimeSpan.FromSeconds(32));
        fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });
        int reservedVotes = fixture.Validation.PendingVotes;

        await fixture.Validation.ValidateAsync(fixture.Sender, malformed);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reservedVotes, Is.Zero, "fixture: the reservation expired before dispatch");
        Assert.That(fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(2)), Is.EqualTo(MessageValidity.Throttled), "the peer's next vote is refused");
    }

    [Test]
    public async Task Block_from_a_new_peer_with_a_rejected_vote_is_still_admitted()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Rejected);

        fixture.Receive(fixture.Sender, BlockMessage(CurrentSlot));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(2)), Is.EqualTo(MessageValidity.Throttled), "fixture: the peer's votes are refused");
        Assert.That(fixture.Raised, Has.Count.EqualTo(2), "its block is validated");
    }

    // Only a vote's verdict promotes or marks its peer for votes.
    [Test]
    public async Task Block_verdict_neither_promotes_nor_marks_its_peer_for_votes([Values(MessageValidity.Accepted, MessageValidity.Rejected)] MessageValidity validity)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        fixture.Receive(fixture.Sender, BlockMessage(CurrentSlot));
        fixture.Raised.Single().Complete(validity);

        MessageValidity[] votes = [.. Enumerable.Range(1, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];

        Assert.That(votes, Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Deferred, MessageValidity.Throttled }), "the peer's votes are admitted, two at a time");
    }

    // Only a rejected vote demotes its peer: nothing refused for load, ignored, or rejected but not a vote is held against it.
    [Test]
    public async Task Peer_with_an_accepted_vote_is_demoted_only_by_a_rejected_vote(
        [Values("vote refused at admission", "vote refused for load", "vote ignored", "block rejected", "oversized message rejected")] string outcome)
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Accepted);
        switch (outcome)
        {
            case "vote refused at admission":
                (GossipVerdict? Raised, Task Validation)[] share = [.. Enumerable.Range(10, fixture.Validation.MaxPendingVotesPerSource).Select(index => fixture.SendVote(fixture.Sender, (ulong)index))];
                Assert.That(fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(2)), Is.EqualTo(MessageValidity.Throttled), "fixture: the peer's share is full");
                foreach ((GossipVerdict? raised, Task validation) in share)
                {
                    raised!.Complete(MessageValidity.Ignored);
                    await validation.WaitAsync(TimeSpan.FromSeconds(5));
                }

                break;
            case "vote refused for load":
                await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 2, MessageValidity.Throttled);
                break;
            case "vote ignored":
                await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 2, MessageValidity.Ignored);
                break;
            case "block rejected":
                fixture.Receive(fixture.Sender, BlockMessage(CurrentSlot));
                fixture.Raised[^1].Complete(MessageValidity.Rejected);
                Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender }), "fixture: the block is rejected");
                break;
            case "oversized message rejected":
                Message oversized = new() { Topic = DeferredFixture.AggregateTopic, Data = ByteString.CopyFrom(Enumerable.Repeat((byte)0xff, DeferredGossipValidation.MaxVoteMessageBytes + 1).ToArray()) };
                fixture.Validation.Verify(fixture.Sender, oversized);
                await fixture.Validation.ValidateAsync(fixture.Sender, oversized);
                Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender }), "fixture: the message is rejected");
                break;
        }

        MessageValidity[] votes = [.. Enumerable.Range(20, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];

        Assert.That(votes, Is.All.EqualTo(MessageValidity.Deferred), "the peer keeps its share, past a new peer's two votes");
    }

    // A message past MaxVoteMessageBytes is no vote, whatever its topic: it is held to the main bounds, and its rejection marks nothing.
    [Test]
    public async Task Rejected_oversized_message_on_a_vote_topic_does_not_refuse_its_peers_votes()
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        Message oversized = new() { Topic = DeferredFixture.AggregateTopic, Data = ByteString.CopyFrom(Enumerable.Repeat((byte)0xff, DeferredGossipValidation.MaxVoteMessageBytes + 1).ToArray()) };

        MessageValidity admitted = fixture.Validation.Verify(fixture.Sender, oversized);
        int pending = fixture.Validation.Pending;
        await fixture.Validation.ValidateAsync(fixture.Sender, oversized);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((admitted, pending), Is.EqualTo((MessageValidity.Deferred, 1)), "fixture: held to the main bounds");
        Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender }), "fixture: rejected");
        Assert.That(fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(1)), Is.EqualTo(MessageValidity.Deferred), "the peer's votes are still admitted");
    }

    // gloas/p2p-interface.md payload_attestation_message: the same vote queue as aggregates, so the same promotion, demotion, mark and refusals.
    [Test]
    public async Task Payload_attestations_promote_mark_and_are_refused_as_aggregates()
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer),
            payloadAttestations: true);
        (GossipVerdict? first, Task firstValidation) = fixture.SendPayloadAttestation(fixture.Sender, validator: 1);
        (GossipVerdict? queued, Task queuedValidation) = fixture.SendPayloadAttestation(fixture.Sender, validator: 2);
        first!.Complete(MessageValidity.Rejected);
        await firstValidation;
        bool queuedCompletedByRejection = queued!.IsCompleted;
        queued.Complete(MessageValidity.Ignored);
        await queuedValidation.WaitAsync(TimeSpan.FromSeconds(5));
        MessageValidity later = fixture.Validation.Verify(fixture.Sender, DeferredFixture.PayloadAttestation(validator: 3));

        await fixture.CompletePayloadAttestationFromAsync(fixture.Neighbor, validator: 4, MessageValidity.Accepted);
        MessageValidity[] promoted = [.. Enumerable.Range(10, 3).Select(validator => fixture.Validation.Verify(fixture.Neighbor, DeferredFixture.PayloadAttestation((ulong)validator)))];
        await fixture.CompletePayloadAttestationFromAsync(fixture.Neighbor, validator: 5, MessageValidity.Rejected);
        MessageValidity demoted = fixture.Validation.Verify(fixture.Neighbor, DeferredFixture.PayloadAttestation(validator: 6));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(queuedCompletedByRejection, Is.False, "a payload attestation deferred before the rejection is still checked");
        Assert.That(later, Is.EqualTo(MessageValidity.Throttled), "a later payload attestation is refused at admission");
        Assert.That(promoted, Is.All.EqualTo(MessageValidity.Deferred), "an accepted payload attestation promotes its peer past a new peer's two");
        Assert.That(demoted, Is.EqualTo(MessageValidity.Throttled), "a rejected one demotes it, and its later payload attestations are refused at admission");
        Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender, fixture.Neighbor }), "only the rejections count against their peers");
    }

    [Test]
    public async Task Peers_with_a_rejected_vote_are_capped_per_slot()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), maxPendingVotes: 2);
        PeerId[] peers = [.. Enumerable.Range(0, 3).Select(static _ => NewPeerId())];
        for (int i = 0; i < peers.Length; i++)
        {
            await fixture.CompleteVoteFromAsync(peers[i], aggregatorIndex: (ulong)i, MessageValidity.Rejected);
        }

        MessageValidity[] later = [.. peers.Select((peer, i) => fixture.Validation.Verify(peer, DeferredFixture.Vote((ulong)(10 + i))))];

        Assert.That(later, Is.EqualTo(new[] { MessageValidity.Throttled, MessageValidity.Throttled, MessageValidity.Deferred }), "past the cap a peer's votes are admitted");
    }

    // PubsubRouter.Rpc.cs (dotnet-libp2p 7741a1ac) L534-536: a vote throttled at admission leaves no pending, seen or limbo id, so another
    // peer's copy delivered afterwards is validated.
    [Test]
    public async Task Vote_refused_at_admission_leaves_no_id_in_the_router_so_another_peers_copy_is_validated()
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        fixture.Pubsub.GetTopic(DeferredFixture.AggregateTopic);
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Rejected);
        Rpc publish = new();
        publish.Publish.Add(DeferredFixture.Vote(2));
        ulong refusedBefore = Metrics.BeaconChainGossipVotesRefusedAfterRejection;
        int raisedBefore = fixture.Raised.Count;

        fixture.ReceiveRpc(fixture.Sender, publish);
        (int raised, int pending) refused = (fixture.Raised.Count, fixture.Pubsub.PendingValidationCount);
        fixture.ReceiveRpc(fixture.Neighbor, publish);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(refused, Is.EqualTo((raisedBefore, 0)), "the router refuses the marked peer's vote and holds nothing for it");
        Assert.That(Metrics.BeaconChainGossipVotesRefusedAfterRejection - refusedBefore, Is.EqualTo(1UL), "fixture: the vote was refused at admission");
        Assert.That(fixture.Raised, Has.Count.EqualTo(raisedBefore + 1), "another peer's copy delivered afterwards is validated");
        Assert.That(fixture.Pubsub.PendingValidationCount, Is.EqualTo(1), "that copy is the one now pending");
        Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender }), "the refusal counts against nobody");
    }

    // PubsubRouter.Rpc.cs (dotnet-libp2p 7741a1ac) L853-908: an IHAVE id that is neither pending, seen nor in limbo is IWANTed from a gossipsub
    // peer scoring at least GossipThreshold, on a topic in our mesh; a vote throttled at admission is none of these.
    [Test]
    public async Task Ihave_for_a_vote_refused_at_admission_is_iwanted_and_its_answer_is_validated()
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        fixture.Pubsub.GetTopic(DeferredFixture.AggregateTopic);
        (_, List<Rpc> sentToGossipPeer, Action<Rpc> fromGossipPeer) = ConnectSubscribedPeer(fixture.Pubsub, DeferredFixture.AggregateTopic);
        Message vote = DeferredFixture.Vote(2);
        MessageId id = new(Eth2MessageId.Compute(vote.Topic, vote.Data));
        Rpc publish = new();
        publish.Publish.Add(vote);
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Rejected);
        int raisedBefore = fixture.Raised.Count;
        fixture.ReceiveRpc(fixture.Sender, publish);
        Assert.That(fixture.Raised, Has.Count.EqualTo(raisedBefore), "fixture: the marked peer's vote is refused");

        Rpc ihave = new() { Control = new ControlMessage() };
        ihave.Control.Ihave.Add(new ControlIHave { TopicID = DeferredFixture.AggregateTopic, MessageIDs = { ByteString.CopyFrom(id.Bytes) } });
        fromGossipPeer(ihave);
        MessageId[] iwanted = [.. sentToGossipPeer.SelectMany(static rpc => rpc.Control?.Iwant ?? []).SelectMany(static iwant => iwant.MessageIDs).Select(static bytes => new MessageId(bytes.ToByteArray()))];
        fromGossipPeer(publish);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(iwanted, Does.Contain(id), "the refused id is unknown to the router, so the IHAVE gets an IWANT");
        Assert.That(fixture.Raised, Has.Count.EqualTo(raisedBefore + 1), "the IWANT answer is validated and handed to the import worker");
        Assert.That(rejected, Is.EqualTo(new[] { fixture.Sender }), "only the rejected vote counts, against the sender");
    }

    // gloas/p2p-interface.md data_column_sidecar: a sidecar whose block is unseen MAY be queued; the queue must not starve other peers' columns.
    [Test]
    public async Task Columns_parked_by_one_peer_leave_room_for_another_peers_column()
    {
        const int share = ColumnGossipRouter.PendingGloasVerdictsPerSource;
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: share, maxPendingBytes: 1 << 24, TimeSpan.FromSeconds(30), gloasColumns: true);
        byte[] digest = ForkDigest.Compute(Sepolia, Sepolia.GetEpoch(GloasColumnSlot));
        Message Column(ulong index, Hash256 root) => new()
        {
            Topic = GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(index)),
            Data = ByteString.CopyFrom(Snappy.CompressToArray(DataColumnSidecarGloas.Encode(DataColumnSidecarGloasTestFixture.BuildSidecar(index, GloasColumnSlot, root)))),
        };

        List<(MessageValidity Verified, Task Validation)> parked = [];
        for (ulong index = 0; index <= share; index++)
        {
            Message column = Column(index, Keccak.Compute($"unseen block {index}"));
            MessageValidity verified = fixture.Validation.Verify(fixture.Sender, column);
            parked.Add((verified, verified == MessageValidity.Deferred ? fixture.Validation.ValidateAsync(fixture.Sender, column) : Task.CompletedTask));
        }

        MessageValidity neighbor = fixture.Validation.Verify(fixture.Neighbor, Column(share + 1, Keccak.Compute("another unseen block")));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(parked.Select(static entry => entry.Verified), Is.All.EqualTo(MessageValidity.Deferred), "a parked column gives back its reservation");
        Assert.That(parked.Select(static entry => entry.Validation.IsCompleted), Is.EqualTo(Enumerable.Repeat(false, share).Append(true)), "the peer's column past its share is not parked");
        Assert.That(neighbor, Is.EqualTo(MessageValidity.Deferred), "another peer's column is still validated");
        Assert.That(fixture.Validation.PendingColumns, Is.EqualTo(1));
    }

    [Test]
    public async Task Votes_are_held_to_their_own_bound_and_leave_the_room_of_other_gossip()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), maxPendingVotes: 2);
        PeerId[] sources = [fixture.Sender, fixture.Neighbor, NewPeerId()];
        for (int i = 0; i < sources.Length; i++)
        {
            await fixture.CompleteVoteFromAsync(sources[i], aggregatorIndex: (ulong)(i + 1), MessageValidity.Accepted);
        }

        string aggregates = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof);
        // One vote from each of three peers with an accepted vote: the third peer's share is free, so only the vote bound refuses its vote.
        MessageValidity[] votes = [.. sources.Select((peer, i) => fixture.Validation.Verify(peer, new Message { Topic = aggregates, Data = ByteString.CopyFrom([(byte)i]) }))];

        MessageValidity block = fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(votes, Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Deferred, MessageValidity.Throttled }), "the vote bound refuses the third vote");
        Assert.That(block, Is.EqualTo(MessageValidity.Deferred));
        Assert.That((fixture.Validation.PendingVotes, fixture.Validation.Pending), Is.EqualTo((2, 1)), "each deferred message is reserved before it is dispatched");
    }

    [Test]
    public async Task Verdict_not_given_in_time_is_abandoned_and_a_later_copy_is_validated_again()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 8, maxPendingBytes: 1 << 20, TimeSpan.FromMilliseconds(200));
        byte[] block = BlockMessage(CurrentSlot);
        ulong abandonedBefore = Metrics.BeaconChainGossipVerdictsAbandoned;
        ulong lateBefore = Metrics.BeaconChainGossipVerdictsLate;

        fixture.Receive(fixture.Sender, block);
        GossipVerdict abandoned = fixture.Raised.Single();
        await abandoned.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        bool lateApplied = abandoned.Complete(MessageValidity.Accepted);
        for (int i = 0; i < 500 && fixture.Pubsub.PendingValidationCount > 0; i++)
        {
            await Task.Delay(10);
        }

        int pendingAfter = fixture.Pubsub.PendingValidationCount;
        fixture.Receive(fixture.Neighbor, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(lateApplied, Is.False);
        Assert.That(fixture.SentToNeighbor(block), Is.False, "a late verdict forwards nothing");
        Assert.That((pendingAfter, fixture.Validation.PendingBytes), Is.EqualTo((0, 0L)));
        Assert.That(Metrics.BeaconChainGossipVerdictsAbandoned - abandonedBefore, Is.EqualTo(1UL));
        Assert.That(Metrics.BeaconChainGossipVerdictsLate - lateBefore, Is.EqualTo(1UL));
        Assert.That(fixture.Router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1), "the router validated the copy again, as it cached no id for the abandoned message");
    }

    // phase0 p2p-interface.md beacon_block: future slots are IGNOREd and MAY be queued.
    [Test]
    public void Early_next_slot_block_is_ignored_at_once_and_imported_once_its_slot_starts()
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 10));
        GossipRouter router = new(Spec, new SlotClock(Spec, timestamper), LimboLogs.Instance);
        List<GossipVerdict> raised = [];
        router.BeaconBlockReceived += (_, verdict) => raised.Add(verdict);
        GossipVerdict verdict = new(static _ => true, null);

        MessageValidity validity = router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, BlockMessage(CurrentSlot + 1), verdict);
        (bool handedOff, int raisedEarly) = (verdict.IsHandedOff, raised.Count);
        timestamper.UtcNow = timestamper.UtcNow.AddSeconds(Spec.SecondsPerSlot);
        router.ReleaseDueMessages();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((validity, handedOff, raisedEarly), Is.EqualTo((MessageValidity.Ignored, false, 0)), "the caller gives the early block's IGNORE at once");
        Assert.That(raised, Has.Count.EqualTo(1), "the held block is raised once its slot starts");
        Assert.That(raised.SingleOrDefault(), Is.Not.SameAs(verdict), "with a verdict no router waits on");
    }

    [Test]
    public void Message_refused_for_local_load_is_checked_again_when_a_copy_arrives([Values] bool heldForItsSlot)
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 10));
        GossipRouter router = new(Spec, new SlotClock(Spec, timestamper), LimboLogs.Instance);
        int raised = 0;
        router.BeaconBlockReceived += (_, verdict) =>
        {
            if (++raised == 1)
            {
                verdict.Complete(MessageValidity.Throttled);
            }
        };
        ulong slot = heldForItsSlot ? CurrentSlot + 1 : CurrentSlot;

        router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, BlockMessage(slot), new GossipVerdict(static _ => true, null));
        timestamper.UtcNow = timestamper.UtcNow.AddSeconds(Spec.SecondsPerSlot);
        router.ReleaseDueMessages();
        router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, BlockMessage(slot), new GossipVerdict(static _ => true, null));

        Assert.That((raised, router.GetDropCount(GossipDropReason.Duplicate)), Is.EqualTo((2, 0L)));
    }

    [Test]
    public async Task Reservation_of_a_message_never_dispatched_is_released_when_it_expires()
    {
        ManualTimeProvider time = new();
        time.JumpUtc(TimeSpan.FromDays(20_000));
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), time: time);
        string aggregates = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof);
        fixture.Validation.Verify(fixture.Sender, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });
        fixture.Validation.Verify(fixture.Sender, new Message { Topic = aggregates, Data = ByteString.CopyFrom([1]) });
        (int pending, int votes) = (fixture.Validation.Pending, fixture.Validation.PendingVotes);

        time.Advance(TimeSpan.FromSeconds(29));
        fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - 1)) });
        (int pendingBeforeExpiry, int votesBeforeExpiry) = (fixture.Validation.Pending, fixture.Validation.PendingVotes);
        time.Advance(TimeSpan.FromSeconds(3));
        fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - 2)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((pending, votes), Is.EqualTo((1, 1)), "fixture: both messages reserved and never dispatched");
        Assert.That((pendingBeforeExpiry, votesBeforeExpiry), Is.EqualTo((2, 1)), "a reservation lasts as long as the router may still dispatch its message");
        Assert.That((fixture.Validation.Pending, fixture.Validation.PendingVotes), Is.EqualTo((2, 0)), "the expired reservations are released, the later ones kept");
    }

    // A reservation is released to the bound it was charged to at admission, so a peer demoted or promoted while its votes are in flight leaves
    // no count behind in the new-peer bound, its own share or the vote bound.
    [Test]
    public async Task Votes_in_flight_when_their_peer_is_demoted_or_promoted_release_the_bounds_they_were_charged_to([Values] bool demoted)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        if (demoted)
        {
            await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Accepted);
        }

        int NewSourceVotes() => DeferredField<int>(fixture.Validation, "_pendingVotesOfNewSources");
        int inFlight = demoted ? 8 : fixture.Validation.MaxPendingVotesPerNewSource;
        (GossipVerdict? Raised, Task Validation)[] held = [.. Enumerable.Range(10, inFlight).Select(index => fixture.SendVote(fixture.Sender, (ulong)index))];
        int newSourceVotesWhileHeld = NewSourceVotes();
        held[0].Raised!.Complete(demoted ? MessageValidity.Rejected : MessageValidity.Accepted);
        await held[0].Validation.WaitAsync(TimeSpan.FromSeconds(5));
        (GossipVerdict? Raised, Task Validation)[] afterChange = demoted ? [] : [.. Enumerable.Range(30, 3).Select(index => fixture.SendVote(fixture.Sender, (ulong)index))];
        foreach ((GossipVerdict? raised, Task validation) in held[1..].Concat(afterChange))
        {
            raised!.Complete(MessageValidity.Ignored);
            await validation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(newSourceVotesWhileHeld, Is.EqualTo(demoted ? 0 : inFlight), "fixture: charged to the bound of the peer's standing at admission");
        Assert.That(NewSourceVotes(), Is.Zero, "the new-peer bound");
        Assert.That(DeferredField<System.Collections.ICollection>(fixture.Validation, "_pendingBySource"), Is.Empty, "the peer's own share");
        Assert.That(fixture.Validation.PendingVotes, Is.Zero, "the vote bound");
    }

    // Only a rejected vote marks its peer: a vote the import worker ignores or refuses for load leaves a new peer its two votes in flight.
    [Test]
    public async Task Vote_verdict_other_than_rejected_does_not_refuse_a_new_peers_votes([Values(MessageValidity.Ignored, MessageValidity.Throttled)] MessageValidity validity)
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, validity);

        MessageValidity[] votes = [.. Enumerable.Range(2, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(votes, Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Deferred, MessageValidity.Throttled }), "neither refused nor promoted: two votes in flight");
        Assert.That(rejected, Is.Empty, "fixture: nothing counts against the peer");
    }

    // A vote whose verdict is never given, because its check timed out or failed locally, neither marks nor promotes its peer, and an
    // acceptance given after the verdict was abandoned changes nothing.
    [Test]
    public async Task Vote_whose_check_timed_out_or_failed_locally_neither_marks_nor_promotes_its_peer([Values] bool checkFails)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromMilliseconds(100));
        if (checkFails)
        {
            fixture.Router.AggregateAndProofReceived += static (_, _) => throw new InvalidOperationException("local fault");
        }

        (GossipVerdict? raised, Task validation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 1);
        await validation.WaitAsync(TimeSpan.FromSeconds(5));
        bool lateAcceptance = raised!.Complete(MessageValidity.Accepted);

        MessageValidity[] votes = [.. Enumerable.Range(2, 3).Select(index => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote((ulong)index)))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(lateAcceptance, Is.False, "fixture: the verdict was abandoned before the acceptance");
        Assert.That(votes, Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Deferred, MessageValidity.Throttled }), "neither refused nor promoted: two votes in flight");
    }

    // PubsubRouter.CompleteValidation (dotnet-libp2p 7741a1ac) takes the router's monitor, under which the router also admits. A vote's verdict
    // changes its peer's standing before the router applies it, so an admission the router orders after the verdict sees the change.
    [Test]
    public async Task Vote_verdict_changes_its_peers_standing_before_the_router_applies_it([Values] bool accepted)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        (GossipVerdict? vote, Task validation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 1);
        if (accepted)
        {
            await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 2, MessageValidity.Rejected);
        }

        LruKeyCache<PeerId> promoted = DeferredField<LruKeyCache<PeerId>>(fixture.Validation, "_acceptedVoteSources");
        HashSet<PeerId> marked = DeferredField<HashSet<PeerId>>(fixture.Validation, "_rejectedVoteSources");
        Lock marks = RejectedVoteSourcesLock(fixture.Validation);
        bool Changed()
        {
            using (marks.EnterScope()) return accepted ? promoted.Get(fixture.Sender) && !marked.Contains(fixture.Sender) : marked.Contains(fixture.Sender);
        }

        Task completing;
        bool changedWhileRouterBusy;
        MessageValidity admission;
        lock (fixture.Pubsub)
        {
            completing = Task.Run(() => vote!.Complete(accepted ? MessageValidity.Accepted : MessageValidity.Rejected));
            changedWhileRouterBusy = SpinWait.SpinUntil(Changed, TimeSpan.FromSeconds(5));
            admission = fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(3));
        }

        await completing.WaitAsync(TimeSpan.FromSeconds(5));
        await validation.WaitAsync(TimeSpan.FromSeconds(5));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(changedWhileRouterBusy, Is.True, "the peer is promoted or marked while the router still waits to apply the verdict");
        Assert.That(admission, Is.EqualTo(accepted ? MessageValidity.Deferred : MessageValidity.Throttled), "an admission ordered after the verdict sees the peer's new standing");
    }

    internal static async Task AssertDeferredPeerPenaltyAsync(string name, byte[] payload, Func<GossipVerdict, Task> validate, MessageValidity expected)
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        string topic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), name);
        using PubsubRouter pubsub = new(new PeerStore(), GossipScoring.Configure(p2p.PubsubSettingsForTest, [topic], Spec));
        pubsub.GetTopic(topic);
        (PeerId sender, _, Action<Rpc> receive) = ConnectSubscribedPeer(pubsub, topic);
        (PeerId neighbor, List<Rpc> forwarded, _) = ConnectSubscribedPeer(pubsub, topic);
        IRoutingStateContainer routing = pubsub;
        routing.Mesh[topic].UnionWith([sender, neighbor]);
        TaskCompletionSource<GossipVerdict> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MessageValidity? given = null;
        pubsub.VerifyMessage = static (_, _) => MessageValidity.Deferred;
        pubsub.OnDeferredMessage = (_, message) =>
        {
            GossipVerdict verdict = new(validity =>
            {
                given = validity;
                return pubsub.CompleteValidation(message, validity);
            }, null);
            pending.SetResult(verdict);
            return verdict.Completion;
        };
        Rpc rpc = new();
        rpc.Publish.Add(new Message { Topic = topic, Data = ByteString.CopyFrom(payload) });
        receive(rpc);
        GossipVerdict deferred = await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(forwarded.SelectMany(static rpc => rpc.Publish), Is.Empty, "pending messages are not forwarded");
        await validate(deferred);
        await pubsub.Heartbeat();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(given, Is.EqualTo(expected), "the worker must distinguish invalid input from unavailable local data");
        Assert.That(forwarded.SelectMany(static rpc => rpc.Publish).Select(static message => message.Data.ToByteArray()),
            Is.EqualTo(expected == MessageValidity.Accepted ? new[] { payload } : []));
        Assert.That(routing.Mesh[topic].Contains(sender), Is.EqualTo(expected != MessageValidity.Rejected), "invalid-message scoring charges the delivering peer");
        Assert.That(routing.Mesh[topic], Does.Contain(neighbor), "a peer that did not deliver the invalid message keeps its score");
    }

    // The router admits on its own thread while the import worker hands verdicts back, so admission reads the rejection marks under their lock.
    [Test]
    public async Task Admission_of_a_new_peers_vote_reads_the_rejection_marks_under_their_lock()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        Lock marks = RejectedVoteSourcesLock(fixture.Validation);
        Task<MessageValidity> admission;
        bool admittedWhileHeld;
        using (marks.EnterScope())
        {
            admission = Task.Run(() => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(1)));
            admittedWhileHeld = admission.Wait(TimeSpan.FromMilliseconds(300));
        }

        MessageValidity validity = await admission.WaitAsync(TimeSpan.FromSeconds(5));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admittedWhileHeld, Is.False, "admission waits while a verdict holds the marks");
        Assert.That(validity, Is.EqualTo(MessageValidity.Deferred), "fixture: the vote is admitted once the lock is free");
    }

    // A peer's promotion is ordered against a concurrent rejection of its vote, so a mark cannot land after the acceptance that cleared it.
    [Test]
    public async Task Acceptance_promotes_its_peer_under_the_rejection_marks_lock()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        (GossipVerdict? valid, Task validation) = fixture.SendVote(fixture.Sender, aggregatorIndex: 1);
        Lock marks = RejectedVoteSourcesLock(fixture.Validation);
        Task accepting;
        bool acceptedWhileHeld;
        using (marks.EnterScope())
        {
            accepting = Task.Run(() => valid!.Complete(MessageValidity.Accepted));
            acceptedWhileHeld = accepting.Wait(TimeSpan.FromMilliseconds(300));
        }

        await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        await validation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(acceptedWhileHeld, Is.False, "the acceptance waits while another verdict holds the marks");
    }

    // A promoted peer carries no mark: its promotion clears the mark and a rejection demotes it in the same step, so admitting its votes neither
    // reads the marks nor waits for their lock.
    [Test]
    public async Task Admission_of_a_promoted_peers_vote_does_not_wait_for_the_rejection_marks_lock()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        await fixture.CompleteVoteFromAsync(fixture.Sender, aggregatorIndex: 1, MessageValidity.Accepted);
        Lock marks = RejectedVoteSourcesLock(fixture.Validation);
        Task<MessageValidity> admission;
        bool admittedWhileHeld;
        using (marks.EnterScope())
        {
            admission = Task.Run(() => fixture.Validation.Verify(fixture.Sender, DeferredFixture.Vote(2)));
            admittedWhileHeld = admission.Wait(TimeSpan.FromSeconds(5));
        }

        MessageValidity validity = await admission.WaitAsync(TimeSpan.FromSeconds(5));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admittedWhileHeld, Is.True, "a promoted peer's vote is admitted while a verdict holds the marks");
        Assert.That(validity, Is.EqualTo(MessageValidity.Deferred));
    }

    private static Lock RejectedVoteSourcesLock(DeferredGossipValidation validation) =>
        (Lock)typeof(DeferredGossipValidation).GetField("_rejectedVoteSourcesLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(validation)!;

    private static T DeferredField<T>(DeferredGossipValidation validation, string name) =>
        (T)typeof(DeferredGossipValidation).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(validation)!;

    private static PeerId NewPeerId() => new Identity(privateKey: null, Nethermind.Libp2p.Core.Dto.KeyType.Secp256K1).PeerId;

    private static readonly ulong GloasColumnSlot = FirstGloasSlot + 1;
    private static readonly string BlockTopic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconBlock);

    private sealed class DeferredFixture(IContainer container, PubsubRouter pubsub, GossipRouter router, DeferredGossipValidation validation, List<GossipVerdict> raised,
        (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) sender, (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) neighbor) : IAsyncDisposable
    {
        public PubsubRouter Pubsub { get; } = pubsub;
        public GossipRouter Router { get; } = router;
        public DeferredGossipValidation Validation { get; } = validation;
        public List<GossipVerdict> Raised { get; } = raised;
        public PeerId Sender { get; } = sender.Peer;
        public PeerId Neighbor { get; } = neighbor.Peer;
        public ManualTimestamper Timestamper { get; private init; } = null!;

        public static async Task<DeferredFixture> Create(int maxPending, long maxPendingBytes, TimeSpan timeout, string protocol = PubsubRouter.GossipsubProtocolVersionV11, int maxPendingVotes = 1024,
            TimeProvider? time = null, bool gloasColumns = false, Action<PeerId, string>? reportRejected = null, bool payloadAttestations = false)
        {
            IContainer container = BeaconChainTestContainer.Builder().Build();
            PubsubSettings settings;
            await using (BeaconP2P p2p = container.Resolve<BeaconP2P>())
            {
                settings = p2p.PubsubSettingsForTest;
            }

            PubsubRouter pubsub = new(new PeerStore(), settings);
            ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 6));
            SlotClock clock = new(Spec, timestamper);
            GossipRouter router = new(Spec, clock, LimboLogs.Instance);
            List<GossipVerdict> raised = [];
            router.BeaconBlockReceived += (_, verdict) => raised.Add(verdict);
            router.AggregateAndProofReceived += (_, verdict) => raised.Add(verdict);
            GossipMessageValidator validator = new(router, new ColumnGossipRouter(Spec, clock, LimboLogs.Instance), Spec, clock);
            if (gloasColumns)
            {
                // No store holds a block, so every Gloas sidecar for the current slot is parked.
                SlotClock gloasClock = new(Sepolia, new ManualTimestamper(SepoliaSlotStart(GloasColumnSlot).AddSeconds(6)));
                ColumnGossipRouter columns = new(Sepolia, gloasClock, LimboLogs.Instance);
                columns.Start(_ => new GossipDigestWindowTests.SilentTopic(), ForkDigest.Compute(Sepolia, Sepolia.GetEpoch(GloasColumnSlot)),
                    [.. Enumerable.Range(0, (int)Eip7594DasConstants.DataColumnSidecarSubnetCount).Select(static subnet => (ulong)subnet)]);
                validator = new(new GossipRouter(Sepolia, gloasClock, LimboLogs.Instance), columns, Sepolia, gloasClock);
            }
            else if (payloadAttestations)
            {
                // The voted block is held, so a payload attestation for it passes every check before the import worker.
                SlotClock ptcClock = new(Sepolia, new ManualTimestamper(SepoliaSlotStart(PtcSlot).AddSeconds(9)));
                BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
                store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
                GossipRouter ptcRouter = new(Sepolia, ptcClock, LimboLogs.Instance, store);
                ptcRouter.PayloadAttestationMessageReceived += (_, verdict) => raised.Add(verdict);
                validator = new(ptcRouter, new ColumnGossipRouter(Sepolia, ptcClock, LimboLogs.Instance), Sepolia, ptcClock);
            }

            DeferredGossipValidation validation = new(pubsub, validator,
                maxPending, maxPendingBytes, maxPendingVotes, timeout, LimboLogs.Instance.GetClassLogger<GossipRouterTests>(), CancellationToken.None, time)
            {
                ReportRejectedMessage = reportRejected,
            };
            pubsub.VerifyMessage = validation.Verify;
            pubsub.OnDeferredMessage = validation.ValidateAsync;
            pubsub.GetTopic(BlockTopic);
            (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) sender = ConnectSubscribedPeer(pubsub, BlockTopic, protocol);
            (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) neighbor = ConnectSubscribedPeer(pubsub, BlockTopic, protocol);
            ((IRoutingStateContainer)pubsub).Mesh[BlockTopic].UnionWith([sender.Peer, neighbor.Peer]);
            return new DeferredFixture(container, pubsub, router, validation, raised, sender, neighbor) { Timestamper = timestamper };
        }

        public static readonly string AggregateTopic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof);

        /// <summary>Validates one aggregate from <paramref name="peer"/> and gives it <paramref name="validity"/>, as the import worker does once it checked the aggregate.</summary>
        public Task CompleteVoteFromAsync(PeerId peer, ulong aggregatorIndex, MessageValidity validity) => CompleteFromAsync(peer, Vote(aggregatorIndex), validity);

        /// <summary>Accepts one vote from each of as many new peers as the accepted-peer set holds, which evicts every peer accepted before.</summary>
        /// <returns>The new peers, oldest first.</returns>
        public async Task<PeerId[]> EvictFromAcceptedPeersAsync()
        {
            PeerId[] peers = [.. Enumerable.Range(0, DeferredGossipValidation.AcceptedVoteSourcesCapacity).Select(static _ => NewPeerId())];
            for (int i = 0; i < peers.Length; i++)
            {
                await CompleteVoteFromAsync(peers[i], aggregatorIndex: (ulong)(100 + i), MessageValidity.Accepted);
            }

            return peers;
        }

        /// <summary>Validates one aggregate from <paramref name="peer"/>.</summary>
        /// <returns>The verdict handed to the import worker, or <c>null</c> when the aggregate was settled before it; and the validation, which ends with the verdict.</returns>
        public (GossipVerdict? Raised, Task Validation) SendVote(PeerId peer, ulong aggregatorIndex) => Send(peer, Vote(aggregatorIndex));

        /// <summary>An aggregate that passes every check before the import worker.</summary>
        public static Message Vote(ulong aggregatorIndex)
        {
            SignedAggregateAndProof aggregate = CreateAggregate(CurrentSlot);
            aggregate.Message!.AggregatorIndex = aggregatorIndex;
            return new Message { Topic = AggregateTopic, Data = ByteString.CopyFrom(Snappy.CompressToArray(SignedAggregateAndProof.Encode(aggregate))) };
        }

        /// <summary>Validates one payload attestation for the held block from <paramref name="peer"/> and gives it <paramref name="validity"/>.</summary>
        public Task CompletePayloadAttestationFromAsync(PeerId peer, ulong validator, MessageValidity validity) => CompleteFromAsync(peer, PayloadAttestation(validator), validity);

        /// <summary>Validates one payload attestation for the held block from <paramref name="peer"/>, as <see cref="SendVote"/> does an aggregate.</summary>
        public (GossipVerdict? Raised, Task Validation) SendPayloadAttestation(PeerId peer, ulong validator) => Send(peer, PayloadAttestation(validator));

        /// <summary>Validates <paramref name="vote"/> from <paramref name="peer"/> and gives it <paramref name="validity"/>, as the import worker does once it checked the vote.</summary>
        private async Task CompleteFromAsync(PeerId peer, Message vote, MessageValidity validity)
        {
            (GossipVerdict? raised, Task validation) = Send(peer, vote);
            Assert.That(raised, Is.Not.Null, "fixture: the vote passes the checks before the import worker");
            raised!.Complete(validity);
            await validation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        /// <summary>Validates <paramref name="vote"/> from <paramref name="peer"/>.</summary>
        /// <returns>The verdict handed to the import worker, or <c>null</c> when the vote was settled before it; and the validation, which ends with the verdict.</returns>
        private (GossipVerdict? Raised, Task Validation) Send(PeerId peer, Message vote)
        {
            int raisedBefore = Raised.Count;
            Assert.That(Validation.Verify(peer, vote), Is.EqualTo(MessageValidity.Deferred), "fixture: the vote is reserved");
            Task validation = Validation.ValidateAsync(peer, vote);
            return (Raised.Count > raisedBefore ? Raised[^1] : null, validation);
        }

        /// <summary>A payload attestation for the held block, for a fixture created with payload attestations.</summary>
        public static Message PayloadAttestation(ulong validator) => new()
        {
            Topic = GossipTopics.Topic(ForkDigest.Compute(Sepolia, Sepolia.GetEpoch(PtcSlot)), GossipTopics.PayloadAttestationMessage),
            Data = ByteString.CopyFrom(Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, validator)))),
        };

        public void Receive(PeerId from, byte[] data)
        {
            Rpc rpc = new();
            rpc.Publish.Add(new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(data) });
            ReceiveRpc(from, rpc);
        }

        public void ReceiveRpc(PeerId from, Rpc rpc) => (from == Sender ? sender.Receive : neighbor.Receive)(rpc);
        public IReadOnlyList<Rpc> SentTo(PeerId peer) => peer == Sender ? sender.Sent : neighbor.Sent;

        public IEnumerable<MessageId> IdontwantsTo(PeerId peer) =>
            SentTo(peer).SelectMany(static rpc => rpc.Control?.Idontwant ?? []).SelectMany(static idontwant => idontwant.MessageIDs).Select(static id => new MessageId(id.ToByteArray()));

        public bool SentToNeighbor(byte[] data) =>
            neighbor.Sent.SelectMany(static rpc => rpc.Publish).Any(message => message.Topic == BlockTopic && message.Data.Span.SequenceEqual(data));

        public ValueTask DisposeAsync()
        {
            Pubsub.Dispose();
            container.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
