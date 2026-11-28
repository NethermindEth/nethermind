// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V76;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V76;

/// <summary>EIP-8518 wrapper discovery, retrieval, validation, relay and mempool aggregation.</summary>
[Parallelizable(ParallelScope.All)]
public class ProofWrapperRelayTests
{
    [Test]
    public async Task Fetch_ValidWrapper_AdmitsItsTransactions_AndRelaysIt()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
            context.TxPool.Received(1).SubmitTx(Arg.Is<Transaction>(tx => FrameTxClaimVerifier.HasAggregatedWitness(tx)), TxHandlingOptions.None);
            Assert.That(other.Announced, Has.Count.EqualTo(1));
            Assert.That(sender.Announced, Is.Empty, "the wrapper came from it");
            Assert.That(context.ProofCache.Covers(wrapper.Deps), Is.True, "the builder can cover its transactions");
        }
    }

    [Test]
    public async Task Fetch_TransactionsAlreadyPending_HoldsButDoesNotRelay()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.AlreadyKnown);
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
            Assert.That(other.Announced, Is.Empty);
        }
    }

    public enum Mismatch { Length, DepsHash, TxsHash }

    [TestCase(Mismatch.Length)]
    [TestCase(Mismatch.DepsHash)]
    [TestCase(Mismatch.TxsHash)]
    public async Task Fetch_FalseHint_DisconnectsBeforeTheProofIsVerified(Mismatch mismatch)
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);
        WrapperAnnouncement announcement = Announcement(wrapper);
        announcement = mismatch switch
        {
            Mismatch.Length => announcement with { Length = announcement.Length + 1 },
            Mismatch.DepsHash => announcement with { DepsHash = TestItem.KeccakF.ValueHash256 },
            _ => announcement with { TxsHash = TestItem.KeccakF.ValueHash256 },
        };

        await context.Relay.OnAnnouncement(sender, [announcement]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sender.Disconnected, Is.True);
            Assert.That(context.Verifier.Verifications, Is.Zero);
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
        }
    }

    [Test]
    public async Task Fetch_InvalidProof_DiscardsWithoutPenalty_AndIsNotFetchedAgain()
    {
        using Context context = new();
        ProofWrapper wrapper = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(Tx(0, TestItem.KeccakA))!], [Tx(0, TestItem.KeccakA)],
            Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(Tx(0, TestItem.KeccakA))), [1, 2, 3]);
        FakePeer sender = context.Peer(wrapper);

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sender.Disconnected, Is.False);
            Assert.That(sender.Requests, Is.EqualTo(1));
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
            context.TxPool.DidNotReceiveWithAnyArgs().SubmitTx(default!, default);
        }
    }

    /// <summary>A peer serving an invalid wrapper under an honest hash cannot get that hash refused: it is the peer's fault,
    /// and the wrapper is fetched from another announcer.</summary>
    [Test]
    public async Task Fetch_InvalidWrapperUnderAnotherHash_BlamesThePeer_AndKeepsTheHashFetchable()
    {
        using Context context = new();
        ProofWrapper honest = context.Wrapper(Tx(0, TestItem.KeccakA));
        Transaction other = Tx(1, TestItem.KeccakB);
        ProofWrapper invalid = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(other)!], [other],
            [new AggregatedDependency(TestItem.KeccakE.ValueHash256, TestItem.KeccakF.ValueHash256)], [1]);
        FakePeer liar = context.Peer(honest);
        liar.Substitute = invalid.Encoded;
        FakePeer holder = context.Peer(honest);

        await context.Relay.OnAnnouncement(liar, [Announcement(honest)]);
        await context.Relay.OnAnnouncement(holder, [Announcement(honest)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(liar.Disconnected, Is.True);
            Assert.That(() => context.Relay.TryGetHeld(honest.Hash.ValueHash256, out _), Is.True.After(5000, 10));
        }
    }

    [Test]
    public async Task Fetch_NoTransactionPending_DoesNotHoldTheWrapper()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.FeeTooLow);
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        Assert.That(context.Relay.HeldCount, Is.Zero);
    }

    /// <summary>EIP-8518 forbids penalizing a peer for proof failures but allows deprioritizing its fetches: after repeated
    /// failures its announcements are no longer fetched, and it stays connected.</summary>
    [Test]
    public async Task Fetch_RepeatedProofFailures_StopFetchingFromThePeer()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        for (int i = 0; i <= 8; i++)
        {
            Transaction tx = Tx((ulong)i, TestItem.KeccakA);
            ProofWrapper wrapper = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(tx)!], [tx],
                Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx)), [1, 2, 3]);
            peer.Served = wrapper;
            await context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Requests, Is.EqualTo(8));
            Assert.That(peer.Disconnected, Is.False);
        }
    }

    /// <summary>Failing over to another announcer does not bypass its limits: one that sent too many invalid proofs is dropped.</summary>
    [Test]
    public async Task Fetch_FailoverToAnAnnouncerOverItsLimit_SkipsIt()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer empty = context.Peer();
        FakePeer flooder = context.Peer();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        empty.Gate = release.Task;
        Task first = context.Relay.OnAnnouncement(empty, [Announcement(wrapper)]);
        // Joins as the alternate announcer while still within its limits.
        await context.Relay.OnAnnouncement(flooder, [Announcement(wrapper)]);
        for (int i = 0; i < 8; i++)
        {
            Transaction tx = Tx((ulong)(100 + i), TestItem.KeccakA);
            ProofWrapper invalid = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(tx)!], [tx],
                Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx)), [1, 2, 3]);
            flooder.Served = invalid;
            await context.Relay.OnAnnouncement(flooder, [Announcement(invalid)]);
        }

        flooder.Served = wrapper;
        release.SetResult();
        await first;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flooder.Requests, Is.EqualTo(8), "the failover did not fetch from the flooder");
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
        }
    }

    /// <summary>A peer at its concurrency limit keeps the hashes it announced, which are fetched from it once a fetch ends.</summary>
    [Test]
    public async Task Fetch_ConcurrentFetchesFromOnePeer_AreCappedThenDeferred()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        peer.ServeAll = true;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Gate = release.Task;
        List<ProofWrapper> wrappers = [];
        for (int i = 0; i < 6; i++)
        {
            ProofWrapper wrapper = context.Wrapper(Tx((ulong)i, TestItem.KeccakA));
            wrappers.Add(wrapper);
            peer.Held.Add(wrapper);
            _ = context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        }

        Assert.That(() => peer.Requests, Is.EqualTo(4).After(5000, 10));
        release.SetResult();

        Assert.That(() => wrappers.TrueForAll(w => context.Relay.TryGetHeld(w.Hash.ValueHash256, out _)), Is.True.After(5000, 10));
    }

    [Test]
    public async Task Fetch_BusyAnnouncer_FailsOverAtOnceToAnotherAnnouncer()
    {
        using Context context = new();
        FakePeer busy = context.Peer();
        busy.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        for (int i = 0; i < 4; i++) _ = context.Relay.OnAnnouncement(busy, [Announcement(context.Wrapper(Tx((ulong)(10 + i), TestItem.KeccakA)))]);
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer other = context.Peer(wrapper);

        _ = context.Relay.OnAnnouncement(busy, [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(other, [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(busy.Requests, Is.EqualTo(4));
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
        }
    }

    /// <summary>A removed peer's fetches go to their next announcer, and those it alone announced are forgotten, so a later
    /// announcement fetches them.</summary>
    [Test]
    public async Task RemovePeer_ReassignsItsFetches_AndForgetsThoseWithoutAnnouncers()
    {
        using Context context = new();
        FakePeer leaving = context.Peer();
        leaving.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        ProofWrapper shared = context.Wrapper(Tx(0, TestItem.KeccakA));
        ProofWrapper sole = context.Wrapper(Tx(1, TestItem.KeccakA));
        FakePeer other = context.Peer(shared);
        _ = context.Relay.OnAnnouncement(leaving, [Announcement(shared)]);
        _ = context.Relay.OnAnnouncement(other, [Announcement(shared)]);
        for (int i = 0; i < 4; i++) _ = context.Relay.OnAnnouncement(leaving, [Announcement(context.Wrapper(Tx((ulong)(10 + i), TestItem.KeccakA)))]);
        _ = context.Relay.OnAnnouncement(leaving, [Announcement(sole)]);

        context.Relay.RemovePeer(leaving);

        Assert.That(() => context.Relay.TryGetHeld(shared.Hash.ValueHash256, out _), Is.True.After(5000, 10));
        Assert.That(context.Relay.FetchCount, Is.Zero);
        await context.Relay.OnAnnouncement(context.Peer(sole), [Announcement(sole)]);
        Assert.That(context.Relay.TryGetHeld(sole.Hash.ValueHash256, out _), Is.True);
    }

    [Test]
    public async Task Fetch_RepeatedTimeouts_StopFetchingFromThePeer()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        peer.TimesOut = true;
        for (int i = 0; i <= 8; i++) await context.Relay.OnAnnouncement(peer, [Announcement(context.Wrapper(Tx((ulong)i, TestItem.KeccakA)))]);

        Assert.That(peer.Requests, Is.EqualTo(8));
    }

    /// <summary>One peer opens at most its share of fetches, so the global bound leaves room for the others.</summary>
    [Test]
    public void Announcements_BeyondAPeersShare_AreNotRecorded_AndOthersStillAre()
    {
        using Context context = new();
        FakePeer flooder = context.Peer();
        flooder.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        FakePeer other = context.Peer();
        other.Gate = flooder.Gate;

        _ = context.Relay.OnAnnouncement(flooder, Announcements(0, 600));
        int flooded = context.Relay.FetchCount;
        _ = context.Relay.OnAnnouncement(other, Announcements(1000, 10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flooded, Is.EqualTo(512));
            Assert.That(context.Relay.FetchCount, Is.EqualTo(522));
        }
    }

    /// <summary>With its announcers full, a wanted wrapper takes a new announcer in place of one that failed.</summary>
    [Test]
    public async Task Announcers_WhenFull_ReplaceAFailedOne()
    {
        using Context context = new();
        FakePeer first = context.Peer();
        first.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        _ = context.Relay.OnAnnouncement(first, [Announcement(wrapper)]);
        FakePeer failing = context.Peer();
        failing.TimesOut = true;
        await context.Relay.OnAnnouncement(failing, [Announcement(context.Wrapper(Tx(1, TestItem.KeccakA)))]);
        await context.Relay.OnAnnouncement(failing, [Announcement(wrapper)]);
        for (int i = 0; i < 14; i++) await context.Relay.OnAnnouncement(context.Peer(), [Announcement(wrapper)]);
        FakePeer holder = context.Peer(wrapper);

        await context.Relay.OnAnnouncement(holder, [Announcement(wrapper)]);
        context.Relay.RemovePeer(first);

        Assert.That(() => context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True.After(5000, 10));
    }

    /// <summary>A wrapper whose announcement the bounded record forgot cannot be checked against it, which is no fault of
    /// the peer: it is left to another announcer, without disconnection.</summary>
    [Test]
    public async Task Fetch_ForgottenAnnouncement_DoesNotBlameThePeer()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer peer = context.Peer(wrapper);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Gate = release.Task;
        Task fetch = context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        _ = context.Relay.OnAnnouncement(peer, Announcements(0, 4096));

        release.SetResult();
        await fetch;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Disconnected, Is.False);
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
        }
    }

    [Test]
    public async Task Manifest_ForgottenAnnouncement_DoesNotBlameThePeer()
    {
        using Context context = new();
        context.Relay.AddLocal(context.Wrapper(Tx(0, TestItem.KeccakA)));
        FakePeer peer = context.Peer();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Gate = release.Task;
        WrapperAnnouncement large = new(300_000, TestItem.KeccakF.ValueHash256, default, default);
        peer.Manifests.Add(new ServedManifest(large.Hash, [TestItem.KeccakA.ValueHash256]));
        Task fetch = context.Relay.OnAnnouncement(peer, [large]);
        _ = context.Relay.OnAnnouncement(peer, Announcements(0, 4096));

        release.SetResult();
        await fetch;

        Assert.That(peer.Disconnected, Is.False);
    }

    /// <summary>A node's failures outlive its connection, so reconnecting does not restore its fetches.</summary>
    [Test]
    public async Task Budget_SurvivesReconnection()
    {
        using Context context = new();
        PublicKey node = TestItem.PublicKeyB;
        FakePeer first = context.Peer(nodeId: node);
        first.TimesOut = true;
        for (int i = 0; i < 8; i++) await context.Relay.OnAnnouncement(first, [Announcement(context.Wrapper(Tx((ulong)i, TestItem.KeccakA)))]);
        context.Relay.RemovePeer(first);
        ProofWrapper wrapper = context.Wrapper(Tx(20, TestItem.KeccakA));
        FakePeer again = context.Peer(wrapper, node);

        await context.Relay.OnAnnouncement(again, [Announcement(wrapper)]);

        Assert.That(again.Requests, Is.Zero);
    }

    [Test]
    public void Requests_AreCappedAcrossAllPeers()
    {
        using Context context = new();
        Task gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        List<FakePeer> peers = [];
        for (int p = 0; p < 17; p++)
        {
            FakePeer peer = context.Peer();
            peer.Gate = gate;
            peers.Add(peer);
            for (int i = 0; i < 4; i++) _ = context.Relay.OnAnnouncement(peer, [Announcement(context.Wrapper(Tx((ulong)(p * 10 + i), TestItem.KeccakA)))]);
        }

        // Fresh nodes share only the unreserved three quarters of the cap.
        Assert.That(peers.Sum(peer => peer.Requests), Is.EqualTo(48));
    }

    /// <summary>Many fresh nodes cannot exceed the global cap nor take its reserved part from a node that served a valid proof.</summary>
    [Test]
    public async Task Requests_FreshNodes_CannotStarveAProvenNode()
    {
        using Context context = new();
        ProofWrapper first = context.Wrapper(Tx(600, TestItem.KeccakB));
        FakePeer proven = context.Peer(first);
        await context.Relay.OnAnnouncement(proven, [Announcement(first)]);
        Task gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        List<FakePeer> fresh = [];
        for (int p = 0; p < 30; p++)
        {
            FakePeer flooder = context.Peer();
            flooder.Gate = gate;
            fresh.Add(flooder);
            for (int i = 0; i < 4; i++) _ = context.Relay.OnAnnouncement(flooder, [Announcement(context.Wrapper(Tx((ulong)(p * 10 + i), TestItem.KeccakA)))]);
        }

        proven.Gate = gate;
        _ = context.Relay.OnAnnouncement(proven, [Announcement(context.Wrapper(Tx(500, TestItem.KeccakA)))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(first.Hash.ValueHash256, out _), Is.True);
            Assert.That(fresh.Sum(peer => peer.Requests), Is.EqualTo(48));
            Assert.That(proven.Requests, Is.EqualTo(2), "its first fetch and one from the reserved part");
        }
    }

    /// <summary>Proofs not verified before take from the sender's verification budget; a proof verified before is free.</summary>
    [Test]
    public async Task Verification_IsRateLimitedPerNode_ExceptForKnownProofs()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        ProofWrapper known = context.Wrapper(Tx(100, TestItem.KeccakB));
        context.ProofCache.VerifyDependencyProof(known.DepsHash, Eip8288Constants.AggregatedVk, known.Proof);
        int before = context.Verifier.Verifications;
        peer.Served = known;
        await context.Relay.OnAnnouncement(peer, [Announcement(known)]);
        for (int i = 0; i < 31; i++)
        {
            ProofWrapper wrapper = context.Wrapper(Tx((ulong)i, Keccak.Compute([(byte)i])));
            peer.Served = wrapper;
            await context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Verifier.Verifications - before, Is.EqualTo(30));
            Assert.That(context.Relay.HeldCount, Is.EqualTo(31), "thirty within the budget and the known one");
            Assert.That(peer.Disconnected, Is.False);
        }
    }

    /// <summary>A node whose verification budget is spent is not fetched from, rather than fetched and dropped.</summary>
    [Test]
    public async Task Verification_SpentBudget_StopsFetching()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        for (int i = 0; i < 31; i++)
        {
            ProofWrapper wrapper = context.Wrapper(Tx((ulong)i, Keccak.Compute([(byte)i])));
            peer.Served = wrapper;
            await context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        }

        Assert.That(peer.Requests, Is.EqualTo(30));
    }

    /// <summary>A node out of verification budget is still fetched from for a wrapper whose dependencies have a proof here.</summary>
    [Test]
    public async Task Verification_SpentBudget_StillFetchesAProvenStatement()
    {
        using Context context = new();
        FakePeer peer = await SpentBudgetPeer(context);
        ProofWrapper proven = context.Wrapper(Tx(100, TestItem.KeccakB));
        context.ProofCache.Remember(proven.Deps, proven.DepsHash, proven.Proof);
        context.ProofCache.VerifyDependencyProof(proven.DepsHash, Eip8288Constants.AggregatedVk, proven.Proof);
        peer.Served = proven;

        await context.Relay.OnAnnouncement(peer, [Announcement(proven)]);

        Assert.That(context.Relay.TryGetHeld(proven.Hash.ValueHash256, out _), Is.True);
    }

    /// <summary>A fetch that waited on a spent verification window is scheduled again once the window rolls.</summary>
    [Test]
    public async Task Verification_FetchWaitingOnTheWindow_IsScheduledWhenItRolls()
    {
        using Context context = new();
        FakePeer peer = await SpentBudgetPeer(context);
        Transaction tx = Tx(100, TestItem.KeccakB);
        ProofWrapper wrapper = context.Wrapper(tx);
        peer.Served = wrapper;
        await context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        Assert.That(peer.Requests, Is.EqualTo(30), "the spent window holds the fetch back");

        context.Relay.VerificationWindow = TimeSpan.FromMilliseconds(1);
        await Task.Delay(10);
        context.Relay.Tick();

        Assert.That(() => context.TxPool.ReceivedCalls().Any(c => c.GetArguments()[0] is Transaction submitted && submitted.Hash == tx.Hash),
            Is.True.After(5000, 10));
    }

    /// <summary>With its announcers full, a wanted wrapper takes a new announcer in place of one out of verification budget.</summary>
    [Test]
    public async Task Announcers_WhenFull_ReplaceOneOutOfVerificationBudget()
    {
        using Context context = new();
        FakePeer spent = await SpentBudgetPeer(context);
        ProofWrapper wrapper = context.Wrapper(Tx(100, TestItem.KeccakB));
        FakePeer first = context.Peer();
        first.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        _ = context.Relay.OnAnnouncement(first, [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(spent, [Announcement(wrapper)]);
        for (int i = 0; i < 14; i++) await context.Relay.OnAnnouncement(context.Peer(), [Announcement(wrapper)]);
        FakePeer holder = context.Peer(wrapper);

        await context.Relay.OnAnnouncement(holder, [Announcement(wrapper)]);
        context.Relay.RemovePeer(first);

        Assert.That(() => context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True.After(5000, 10));
    }

    /// <summary>A transaction awaiting cells lets its wrapper circulate but does not offset a rejected one.</summary>
    [Test]
    public async Task Relay_PendingBlobTransaction_DoesNotOffsetARejection()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.FeeTooLow);
        ProofWrapper wrapper = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0), Tx(1, TestItem.KeccakC));
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
            Assert.That(other.Announced, Is.Empty);
        }
    }

    /// <summary>A blob-carrying transaction an earlier wrapper already sent to sampling counts as known, not as pending.</summary>
    [Test]
    public async Task Relay_BlobTransactionAlreadySampled_IsKnown()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.AlreadyKnown);
        ProofWrapper first = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0));
        ProofWrapper second = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0), Tx(1, TestItem.KeccakC));
        await context.Relay.OnAnnouncement(context.Peer(first), [Announcement(first)]);
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(context.Peer(second), [Announcement(second)]);

        Assert.That(other.Announced.Select(a => a.Hash), Does.Not.Contain(second.Hash.ValueHash256));
    }

    /// <summary>A node out of budget fetched from for a proven statement that serves another proof is charged a failure.</summary>
    [Test]
    public async Task Verification_ProvenStatementWithAnotherProof_CountsAgainstThePeer()
    {
        using Context context = new();
        FakePeer peer = await SpentBudgetPeer(context);
        for (int i = 0; i < 9; i++)
        {
            Transaction tx = Tx((ulong)(200 + i), Keccak.Compute([(byte)(100 + i)]));
            List<AggregatedDependency> deps = Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx));
            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
            context.ProofCache.Remember(deps, depsHash, context.Verifier.Prove(depsHash));
            ProofWrapper other = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(tx)!], [tx], deps, [7, 7, 7]);
            peer.Served = other;
            await context.Relay.OnAnnouncement(peer, [Announcement(other)]);
        }

        Assert.That(peer.Requests, Is.EqualTo(30 + 8), "dropped after its eighth failure");
    }

    /// <summary>A wrapper refused only because the global verification budget is spent waits for the window to roll.</summary>
    [Test]
    public async Task Verification_GlobalBudgetSpent_DefersTheFetch()
    {
        using Context context = new();
        for (int p = 0; p < 8; p++)
        {
            FakePeer flooder = context.Peer();
            for (int i = 0; i < 30; i++)
            {
                ProofWrapper wrapper = context.Wrapper(Tx((ulong)(p * 100 + i), Keccak.Compute([(byte)p, (byte)i])));
                flooder.Served = wrapper;
                await context.Relay.OnAnnouncement(flooder, [Announcement(wrapper)]);
            }
        }

        Transaction tx = Tx(5000, TestItem.KeccakB);
        ProofWrapper waiting = context.Wrapper(tx);
        FakePeer late = context.Peer(waiting);
        await context.Relay.OnAnnouncement(late, [Announcement(waiting)]);
        Assert.That(context.Relay.FetchCount, Is.EqualTo(1), "the fetch is kept");

        context.Relay.VerificationWindow = TimeSpan.FromMilliseconds(1);
        await Task.Delay(10);
        context.Relay.Tick();

        Assert.That(() => context.TxPool.ReceivedCalls().Any(c => c.GetArguments()[0] is Transaction submitted && submitted.Hash == tx.Hash),
            Is.True.After(5000, 10));
    }

    /// <summary>A blob-carrying copy the sparse blobpool declines is neither pending nor rejected: the wrapper is not held, but
    /// left to the next announcer rather than discarded.</summary>
    [Test]
    public async Task Fetch_BlobCopyTheRegistryDeclines_LeavesTheWrapperToTheNextAnnouncer()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        using Context context = new(registry: registry);
        ProofWrapper wrapper = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0));
        FakePeer first = context.Peer(wrapper);
        FakePeer second = context.Peer(wrapper);

        await context.Relay.OnAnnouncement(first, [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(second, [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.HeldCount, Is.Zero);
            Assert.That(second.Requests, Is.EqualTo(1), "not discarded, so fetched again");
            Assert.That(first.Disconnected || second.Disconnected, Is.False);
        }
    }

    /// <summary>An honest burst that outruns the verification budget is handed on, never charged as failures.</summary>
    [Test]
    public async Task Verification_BurstBeyondTheBudget_IsNotChargedAsFailures()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        peer.ServeAll = true;
        List<WrapperAnnouncement> burst = [];
        for (int i = 0; i < 38; i++)
        {
            ProofWrapper wrapper = context.Wrapper(Tx((ulong)i, Keccak.Compute([(byte)i])));
            peer.Held.Add(wrapper);
            burst.Add(Announcement(wrapper));
        }

        await context.Relay.OnAnnouncement(peer, burst);
        context.Relay.VerificationWindow = TimeSpan.FromMilliseconds(1);
        await Task.Delay(10);
        ProofWrapper next = context.Wrapper(Tx(100, TestItem.KeccakB));
        peer.Held.Add(next);
        await context.Relay.OnAnnouncement(peer, [Announcement(next)]);

        Assert.That(context.Relay.TryGetHeld(next.Hash.ValueHash256, out _), Is.True, "the peer was not dropped");
    }

    private static async Task<FakePeer> SpentBudgetPeer(Context context)
    {
        FakePeer peer = context.Peer();
        for (int i = 0; i < 30; i++)
        {
            ProofWrapper wrapper = context.Wrapper(Tx((ulong)i, Keccak.Compute([(byte)i])));
            peer.Served = wrapper;
            await context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        }

        return peer;
    }

    [Test]
    public async Task Fetch_ItemLongerThanAnnounced_Disconnects()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer peer = context.Peer(wrapper);
        peer.Substitute = [.. wrapper.Encoded, 0x80];

        await context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Disconnected, Is.True);
            Assert.That(context.Verifier.Verifications, Is.Zero);
        }
    }

    /// <summary>A blob-carrying transaction of a verified wrapper goes to EIP-8070 cell sampling, not straight to the pool,
    /// and the wrapper's sender is not taken for a cell provider.</summary>
    [Test]
    public async Task Fetch_BlobCarryingWrapper_AwaitsCellsWithoutProviders()
    {
        using Context context = new();
        Transaction tx = FrameTxTestFrames.LeanSphincsBlobTx(0);
        ProofWrapper wrapper = context.Wrapper(tx);

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Registry.HasRecordedTransaction(tx.Hash!.ValueHash256), Is.True);
            Assert.That(context.Registry.GetFullProviderAnnouncementCount(tx.Hash!.ValueHash256), Is.Zero);
            context.TxPool.DidNotReceiveWithAnyArgs().SubmitTx(default!, default);
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
        }
    }

    /// <summary>A blob-carrying transaction awaiting its cells is not admitted: the wrapper is held and relayed, as EIP-8518
    /// lets it circulate before availability, but not credited to its sender.</summary>
    [Test]
    public async Task Fetch_BlobTransactionAwaitingCells_IsNotAdmitted()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0));
        FakePeer sender = context.Peer(wrapper);
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);
        Task gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        for (int p = 0; p < 30; p++)
        {
            FakePeer flooder = context.Peer();
            flooder.Gate = gate;
            for (int i = 0; i < 4; i++) _ = context.Relay.OnAnnouncement(flooder, [Announcement(context.Wrapper(Tx((ulong)(p * 10 + i), TestItem.KeccakA)))]);
        }

        sender.Gate = gate;
        _ = context.Relay.OnAnnouncement(sender, [Announcement(context.Wrapper(Tx(500, TestItem.KeccakA)))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
            Assert.That(other.Announced.Select(a => a.Hash), Does.Contain(wrapper.Hash.ValueHash256));
            Assert.That(sender.Requests, Is.EqualTo(1), "no reserved slot without an admitted transaction");
        }
    }

    /// <summary>A copy with other blob metadata cannot touch the copy still awaiting cells; it is recorded only once the
    /// registry no longer holds that one, after its validation failed or it expired.</summary>
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    public async Task Fetch_BlobTransactionCopy_WithOtherMetadata_WaitsForTheRecordedOneToFail(bool stillRecorded, int records)
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        bool recorded = false;
        registry.When(r => r.RecordTransaction(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Transaction>())).Do(_ => recorded = true);
        registry.HasRecordedTransaction(Arg.Any<ValueHash256>()).Returns(_ => recorded && stillRecorded);
        using Context context = new(registry: registry);
        ProofWrapper first = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0, proofFill: 1));
        ProofWrapper second = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0, proofFill: 2), Tx(1, TestItem.KeccakC));
        await context.Relay.OnAnnouncement(context.Peer(first), [Announcement(first)]);

        await context.Relay.OnAnnouncement(context.Peer(second), [Announcement(second)]);

        using (Assert.EnterMultipleScope())
        {
            registry.DidNotReceiveWithAnyArgs().Clear(default);
            registry.ReceivedWithAnyArgs(records).RecordTransaction(default!, default!);
        }
    }

    /// <summary>The differing copy is kept as the alternate and recorded once the recorded copy fails validation.</summary>
    [Test]
    public async Task Fetch_BlobTransactionAlternate_IsRecordedOnceTheRecordedCopyFails()
    {
        ISparseBlobPoolPeerRegistry registry = Substitute.For<ISparseBlobPoolPeerRegistry>();
        bool recorded = false;
        registry.When(r => r.RecordTransaction(Arg.Any<ISparseBlobPoolPeer>(), Arg.Any<Transaction>())).Do(_ => recorded = true);
        registry.HasRecordedTransaction(Arg.Any<ValueHash256>()).Returns(_ => recorded);
        using Context context = new(registry: registry);
        Transaction alternate = FrameTxTestFrames.LeanSphincsBlobTx(0, proofFill: 2);
        ProofWrapper first = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0, proofFill: 1));
        ProofWrapper second = context.Wrapper(alternate, Tx(1, TestItem.KeccakC));
        await context.Relay.OnAnnouncement(context.Peer(first), [Announcement(first)]);
        await context.Relay.OnAnnouncement(context.Peer(second), [Announcement(second)]);
        registry.ReceivedWithAnyArgs(1).RecordTransaction(default!, default!);

        recorded = false;
        context.Relay.Prune();

        using (Assert.EnterMultipleScope())
        {
            registry.ReceivedWithAnyArgs(2).RecordTransaction(default!, default!);
            registry.Received(1).RecordTransaction(Arg.Any<ISparseBlobPoolPeer>(),
                Arg.Is<Transaction>(tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Proofs[0] == 2));
        }
    }

    /// <summary>Another wrapper covering the same blob-carrying transaction adds no provider and does not restart sampling.</summary>
    [Test]
    public async Task Fetch_SecondWrapperOfABlobTransaction_AddsNoProvider()
    {
        using Context context = new();
        Transaction tx = FrameTxTestFrames.LeanSphincsBlobTx(0);
        Transaction sibling = Tx(1, TestItem.KeccakC);
        ProofWrapper first = context.Wrapper(tx);
        ProofWrapper second = context.Wrapper(FrameTxTestFrames.LeanSphincsBlobTx(0), sibling);
        await context.Relay.OnAnnouncement(context.Peer(first), [Announcement(first)]);

        await context.Relay.OnAnnouncement(context.Peer(second), [Announcement(second)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Registry.HasRecordedTransaction(tx.Hash!.ValueHash256), Is.True);
            Assert.That(context.Registry.GetFullProviderAnnouncementCount(tx.Hash!.ValueHash256), Is.Zero);
        }
    }

    /// <summary>Cells are not sought for a blob-carrying transaction before its wrapper's proof verifies.</summary>
    [Test]
    public async Task Fetch_BlobCarryingWrapperWithAnInvalidProof_RecordsNothing()
    {
        using Context context = new();
        Transaction tx = FrameTxTestFrames.LeanSphincsBlobTx(0);
        ProofWrapper wrapper = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(tx)!], [tx],
            Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx)), [1, 2, 3]);

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        Assert.That(context.Registry.HasRecordedTransaction(tx.Hash!.ValueHash256), Is.False);
    }

    /// <summary>An elided blob-carrying frame transaction is sampled only when a verified wrapper covered it: one without
    /// aggregated witnesses goes to the pool as before.</summary>
    [Test]
    public void Registry_SamplesOnlyAggregatedBlobFrameTransactions()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        Transaction raw = FrameTxTestFrames.LeanSphincsBlobTx(0);
        raw.NetworkWrapper = ((ShardBlobNetworkWrapper)raw.NetworkWrapper!) with { Blobs = [] };

        context.Registry.RecordTransaction(peer, raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Registry.HasRecordedTransaction(raw.Hash!.ValueHash256), Is.False);
            context.TxPool.ReceivedWithAnyArgs(1).SubmitTx(default!, default);
        }
    }

    [Test]
    public async Task Announcement_FromARemovedPeer_RecordsNothing()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        context.Relay.RemovePeer(peer);

        await context.Relay.OnAnnouncement(peer, [Announcement(context.Wrapper(Tx(0, TestItem.KeccakA)))]);

        Assert.That(context.Relay.FetchCount, Is.Zero);
    }

    private static List<WrapperAnnouncement> Announcements(int start, int count)
    {
        List<WrapperAnnouncement> announcements = [];
        for (int i = start; i < start + count; i++) announcements.Add(new WrapperAnnouncement(100, Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256, default, default));
        return announcements;
    }

    [Test]
    public void EncodingCache_IsBoundedByBytes()
    {
        ProofWrapperRelay.EncodingCache cache = new(100);
        for (int i = 0; i < 10; i++) cache.Set(Keccak.Compute([(byte)i]).ValueHash256, new byte[40]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Bytes, Is.LessThanOrEqualTo(100));
            Assert.That(cache.TryGet(Keccak.Compute([9]).ValueHash256), Is.Not.Null);
            Assert.That(cache.TryGet(Keccak.Compute([0]).ValueHash256), Is.Null);
        }
    }

    /// <summary>A hash whose announcers are all exhausted is forgotten, so a later announcement fetches it again.</summary>
    [Test]
    public async Task Fetch_HashOutOfAnnouncers_IsFetchedOnALaterAnnouncement()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));

        await context.Relay.OnAnnouncement(context.Peer(), [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
    }

    [Test]
    public async Task Fetch_RetainedWitnessFails_DiscardsTheWrapper()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.Invalid.WithMessage(FrameTxClaimVerifier.InvalidClaim));
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA, withSecp256k1Claim: true));
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
            Assert.That(other.Announced, Is.Empty);
        }
    }

    [Test]
    public async Task Fetch_CompactCacheMiss_RetriesInFull()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);
        sender.CompactReferencesEverything = true;

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sender.CompactFlags, Is.EqualTo(new[] { true, false }));
            Assert.That(sender.Disconnected, Is.False);
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
        }
    }

    [Test]
    public async Task Fetch_EmptyResponse_TriesAnotherAnnouncer()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer empty = context.Peer();
        FakePeer holder = context.Peer(wrapper);
        TaskCompletionSource firstAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        empty.Gate = firstAnswered.Task;

        Task first = context.Relay.OnAnnouncement(empty, [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(holder, [Announcement(wrapper)]);
        firstAnswered.SetResult();
        await first;

        Assert.That(() => context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True.After(5000, 10));
        Assert.That(holder.Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task Announcement_EquivalentCoverage_SkipsTheFetch()
    {
        using Context context = new();
        Transaction tx = Tx(0, TestItem.KeccakA);
        ProofWrapper held = context.Wrapper(tx);
        context.Relay.AddLocal(held);
        FakePeer peer = context.Peer();

        // Different proof bytes, equal coverage.
        await context.Relay.OnAnnouncement(peer, [Announcement(held) with { Hash = TestItem.KeccakF.ValueHash256 }]);

        Assert.That(peer.Requests, Is.Zero);
    }

    [Test]
    public async Task Inactive_IgnoresAnnouncements()
    {
        using Context context = new(Eip8141Prototype.Instance);
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);

        Assert.That(sender.Requests, Is.Zero);
    }

    [Test]
    public void Serve_OnlyAnnouncedWrappers_AndReferencesEncodingsThePeerHolds()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer peer = context.Peer();
        FakePeer late = context.Peer();
        context.Relay.AddLocal(wrapper);
        context.Relay.RemovePeer(late);

        IReadOnlyList<ServedWrapper> full = context.Relay.ServeWrappers(peer, [wrapper.Hash.ValueHash256], compact: false);
        IReadOnlyList<ServedWrapper> compact = context.Relay.ServeWrappers(peer, [wrapper.Hash.ValueHash256], compact: true);
        IReadOnlyList<ServedManifest> manifests = context.Relay.ServeManifests(peer, [wrapper.Hash.ValueHash256, TestItem.KeccakF.ValueHash256]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Announced, Has.Count.EqualTo(1));
            Assert.That(full[0].Item, Is.EqualTo(wrapper.Encoded));
            Assert.That(compact[0].Item, Has.Length.LessThan(wrapper.Length), "the peer holds the encodings of a wrapper served to it");
            Assert.That(manifests, Has.Count.EqualTo(1));
            Assert.That(context.Relay.ServeWrappers(late, [wrapper.Hash.ValueHash256], compact: false), Is.Empty);
        }
    }

    [Test]
    public void Aggregate_PendingRawTransactions_ProducesAVerifyingWrapper()
    {
        using Context context = new();
        Transaction first = Tx(0, TestItem.KeccakA, withSecp256k1Claim: true);
        Transaction second = Tx(1, TestItem.KeccakB);
        context.TxPool.GetPendingTransactions().Returns([first, second]);

        ProofWrapper wrapper = context.Relay.Aggregate()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Transactions, Has.Length.EqualTo(2));
            Assert.That(ProofWrapperCodec.VerifyProof(wrapper, context.ProofCache), Is.True);
            Assert.That(ProofWrapperCodec.TryDecode(wrapper.Encoded, null, wrapper.Length, out ProofWrapper? decoded, out _),
                Is.EqualTo(ProofWrapperCodec.Verdict.Valid));
            Assert.That(decoded!.Hash, Is.EqualTo(wrapper.Hash));
        }
    }

    /// <summary>A reorg returns a transaction with aggregated witnesses to the pool; the proof of the block it came from
    /// covers it, so it is re-aggregated into a wrapper peers can fetch.</summary>
    [Test]
    public void Aggregate_ReorgedAggregatedTransaction_IsReaggregatedFromItsBlocksProof()
    {
        using Context context = new();
        Transaction reorged = Tx(0, TestItem.KeccakA, withSecp256k1Claim: true);
        Transaction sibling = Tx(1, TestItem.KeccakB);
        Block block = Build.A.Block.WithTransactions(reorged, sibling).TestObject;
        List<AggregatedDependency> blockDeps = AggregatedDependencyProof.Aggregated(FrameTxDependencyProof.BlockDependencies(block));
        context.Relay.RememberBlockProof(block, context.Verifier.Prove(Eip8288Dependencies.ComputeDepsHash(blockDeps)));
        reorged.ClaimWitnesses![0] = FrameTxClaimVerifier.AggregatedWitness;
        context.TxPool.GetPendingTransactions().Returns([reorged]);

        ProofWrapper wrapper = context.Relay.Aggregate()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Manifest, Is.EqualTo(new[] { reorged.Hash }));
            Assert.That(ProofWrapperCodec.VerifyProof(wrapper, context.ProofCache), Is.True);
            Assert.That(context.Verifier.LastInput!.RecursiveProofs, Has.Count.EqualTo(1), "extends the block's proof");
            Assert.That(context.Verifier.LastInput.Discards, Has.Count.EqualTo(1), "and discards the sibling's dependency");
        }
    }

    [Test]
    public void Aggregate_AggregatedTransactionNoProofCovers_IsLeftOut()
    {
        using Context context = new();
        Transaction orphan = Tx(0, TestItem.KeccakA);
        orphan.ClaimWitnesses![0] = FrameTxClaimVerifier.AggregatedWitness;
        context.TxPool.GetPendingTransactions().Returns([orphan]);

        Assert.That(context.Relay.Aggregate(), Is.Null);
    }

    private static Transaction Tx(ulong nonce, Hash256 message, bool withSecp256k1Claim = false) =>
        FrameTxTestFrames.LeanSphincsTx(nonce, withSecp256k1Claim, (TestItem.KeccakA, message));

    private static WrapperAnnouncement Announcement(ProofWrapper wrapper) =>
        new(wrapper.Length, wrapper.Hash.ValueHash256, wrapper.DepsHash, wrapper.TxsHash);

    private sealed class Context : IDisposable
    {
        public Context(IReleaseSpec? spec = null, ISparseBlobPoolPeerRegistry? registry = null)
        {
            TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.Accepted);
            TxPool.ValidateTxForBlobSampling(default!).ReturnsForAnyArgs(AcceptTxResult.Accepted);
            TxPool.GetPendingTransactions().Returns([]);
            ProofCache = new DependencyProofCache(Verifier, LimboLogs.Instance);
            Registry = new SparseBlobPoolPeerRegistry(TxPool, new BlobCustodyTracker(), RunImmediatelyScheduler.Instance, LimboLogs.Instance);
            Relay = new ProofWrapperRelay(TxPool, ProofCache, new TestSingleReleaseSpecProvider(spec ?? Eip8288Prototype.Instance),
                Substitute.For<IBlockTree>(), Timestamper.Default, new BlocksConfig { DependencyProofAggregation = DependencyProofAggregation.Mempool },
                LimboLogs.Instance, registry ?? Registry);
        }

        public SparseBlobPoolPeerRegistry Registry { get; }

        public ITxPool TxPool { get; } = Substitute.For<ITxPool>();
        public FakeVerifier Verifier { get; } = new();
        public DependencyProofCache ProofCache { get; }
        public ProofWrapperRelay Relay { get; }

        public FakePeer Peer(ProofWrapper? served = null, PublicKey? nodeId = null)
        {
            FakePeer peer = new(served) { NodeId = nodeId ?? new PrivateKeyGenerator().Generate().PublicKey };
            Registry.AddPeer(peer);
            Relay.AddPeer(peer);
            return peer;
        }

        public ProofWrapper Wrapper(params Transaction[] transactions)
        {
            Array.Sort(transactions, static (left, right) => left.Hash!.CompareTo(right.Hash!));
            List<AggregatedDependency> deps = [];
            byte[][] encodings = new byte[transactions.Length][];
            for (int i = 0; i < transactions.Length; i++)
            {
                deps.AddRange(ProofWrapperCodec.AggregatedDependencies(transactions[i]));
                encodings[i] = ProofWrapperCodec.Normalize(transactions[i])!;
            }

            List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
            return ProofWrapperCodec.Create(encodings, transactions, canonical, Verifier.Prove(Eip8288Dependencies.ComputeDepsHash(canonical)));
        }

        public void Dispose()
        {
            Relay.Dispose();
            Registry.Dispose();
        }
    }

    /// <summary>A Lean backend whose proof of a statement is its dependency digest.</summary>
    private sealed class FakeVerifier : ILeanProofVerifier
    {
        private int _verifications;

        public int Verifications => Volatile.Read(ref _verifications);
        public AggregationInput? LastInput { get; private set; }

        public byte[] Prove(in ValueHash256 depsHash) => depsHash.ToByteArray();

        public void EnsureAvailable() { }

        public bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness) => true;

        public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
        {
            Interlocked.Increment(ref _verifications);
            return proof.SequenceEqual(depsHash.Bytes);
        }

        public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            LastInput = input;
            return Prove(depsHash);
        }
    }

    /// <summary>An <c>eth/76</c> peer serving one wrapper.</summary>
    private sealed class FakePeer(ProofWrapper? served) : IProofWrapperPeer, ISparseBlobPoolPeer
    {
        PublicKey ISparseBlobPoolPeer.Id => NodeId;
        bool ISparseBlobPoolPeer.IsClosing => Disconnected;
        bool ISparseBlobPoolPeer.TrySendGetCells(in ValueHash256 hash, BlobCellMask requestMask) => true;
        bool ISparseBlobPoolPeer.TrySendPooledTransactionRequest(Hash256 hash) => true;
        void ISparseBlobPoolPeer.MaintainSparseBlobState(DateTimeOffset now) { }
        void ISparseBlobPoolPeer.DisconnectSparseBlobPeer(DisconnectReason reason, string details) => Disconnected = true;

        private int _requests;

        public ProofWrapper? Served { get; set; } = served;

        public PublicKey NodeId { get; init; } = TestItem.PublicKeyA;

        /// <summary>Serves every requested wrapper of <see cref="Held"/>.</summary>
        public bool ServeAll { get; set; }

        public List<ProofWrapper> Held { get; } = [];

        /// <summary>An item served in place of <see cref="Served"/>'s, under its hash.</summary>
        public byte[]? Substitute { get; set; }

        public List<WrapperAnnouncement> Announced { get; } = [];
        public List<bool> CompactFlags { get; } = [];
        public bool CompactReferencesEverything { get; set; }
        public bool TimesOut { get; set; }
        public bool Disconnected { get; set; }
        public Task Gate { get; set; } = Task.CompletedTask;
        public int Requests => Volatile.Read(ref _requests);

        public void Announce(IReadOnlyList<WrapperAnnouncement> announcements) => Announced.AddRange(announcements);

        public async Task<IReadOnlyList<ServedWrapper>> GetProofWrappers(IReadOnlyList<ValueHash256> hashes, bool compact, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            CompactFlags.Add(compact);
            await Gate;
            if (TimesOut) throw new TimeoutException();
            if (ServeAll)
            {
                List<ServedWrapper> all = [];
                foreach (ValueHash256 hash in hashes)
                {
                    ProofWrapper? held = Held.Find(w => w.Hash.ValueHash256 == hash);
                    if (held is not null) all.Add(new ServedWrapper(hash, held.Encoded));
                }

                return all;
            }

            if (Served is not { } wrapper || !Enumerable.Contains(hashes, wrapper.Hash.ValueHash256)) return [];
            byte[] item = Substitute ?? (compact && CompactReferencesEverything
                ? ProofWrapperCodec.Encode(wrapper.EncodedTransactions, wrapper.Deps, wrapper.Proof, static _ => true)
                : wrapper.Encoded);
            return [new ServedWrapper(wrapper.Hash.ValueHash256, item)];
        }

        public List<ServedManifest> Manifests { get; } = [];

        public async Task<IReadOnlyList<ServedManifest>> GetProofWrapperManifests(IReadOnlyList<ValueHash256> hashes, CancellationToken token)
        {
            await Gate;
            return Manifests.FindAll(m => Enumerable.Contains(hashes, m.Hash));
        }

        public void Disconnect(string reason) => Disconnected = true;
    }
}
