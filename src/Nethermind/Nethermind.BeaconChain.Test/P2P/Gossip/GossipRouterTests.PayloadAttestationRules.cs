// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

/// <summary>The gloas/p2p-interface.md <c>payload_attestation_message</c> rules that need no beacon state.</summary>
public partial class GossipRouterTests
{
    private static readonly ulong PtcSlot = FirstGloasSlot + 1;
    private static readonly Hash256 PtcBlockRoot = Keccak.Compute("ptc voted block");
    private const ulong PtcValidator = 7;

    [Test]
    public async Task Payload_attestation_failed_block_respects_prior_ignores([Values(-1, 0, 1)] int slotOffset, [Values] bool verified, [Values] bool held)
    {
        FailedBlockRoots failed = new();
        failed.Add(PtcBlockRoot, PtcSlot);
        ManualTimestamper time = new(SepoliaSlotStart(PtcSlot).AddSeconds(6));
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        if (held) store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, time), LimboLogs.Instance, store, failedBlocks: failed);
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => received++;
        PayloadAttestationMessage vote = PtcVote(PtcBlockRoot, (ulong)((long)PtcSlot + slotOffset));
        if (verified)
        {
            router.MarkPayloadAttestationVerified(vote);
        }

        MessageValidity expected = !verified && slotOffset == 0 ? MessageValidity.Rejected : MessageValidity.Ignored;
        byte[] payload = Snappy.CompressToArray(PayloadAttestationMessage.Encode(vote));
        await AssertDeferredPeerPenaltyAsync(GossipTopics.PayloadAttestationMessage, payload, verdict =>
        {
            verdict.Complete(router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, payload, verdict));
            return Task.CompletedTask;
        }, expected);

        GossipDropReason reason = verified ? GossipDropReason.Duplicate
            : slotOffset < 0 ? GossipDropReason.StaleSlot
            : slotOffset > 0 ? GossipDropReason.FutureSlot
            : GossipDropReason.InvalidField;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.Zero, "failed blocks are refused before a vote can reach signature validation");
            Assert.That(router.GetDropCount(reason), Is.EqualTo(1), "a failed root proves the block was seen even when it was never stored");
        }
    }

    public enum PtcCase
    {
        Consumed,
        PreGloasSlot,
        AlreadySeen,
        SeenForAnotherValidator,
        PreviousSlot,
        NextSlot,
        BlockNotHeld,
        BlockAtAnotherSlot,
        Oversized,
    }

    [TestCase(PtcCase.Consumed, null, false)]
    [TestCase(PtcCase.PreGloasSlot, GossipDropReason.InvalidField, true)]
    [TestCase(PtcCase.AlreadySeen, GossipDropReason.Duplicate, false)]
    [TestCase(PtcCase.SeenForAnotherValidator, null, false)]
    [TestCase(PtcCase.PreviousSlot, GossipDropReason.StaleSlot, false)]
    [TestCase(PtcCase.NextSlot, GossipDropReason.FutureSlot, false)]
    [TestCase(PtcCase.BlockNotHeld, GossipDropReason.UnknownBlock, false)]
    [TestCase(PtcCase.BlockAtAnotherSlot, GossipDropReason.InvalidField, false)]
    [TestCase(PtcCase.Oversized, GossipDropReason.Oversized, true)]
    public void Payload_attestation_message_rules_needing_no_state(PtcCase testCase, GossipDropReason? reason, bool rejected)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => received++;
        ulong blockSlot = testCase == PtcCase.BlockAtAnotherSlot ? PtcSlot - 1 : PtcSlot;
        if (testCase != PtcCase.BlockNotHeld)
            store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(blockSlot)));
        ulong dataSlot = testCase switch
        {
            PtcCase.PreGloasSlot => FirstGloasSlot - 1,
            PtcCase.PreviousSlot => PtcSlot - 1,
            PtcCase.NextSlot => PtcSlot + 1,
            _ => PtcSlot,
        };
        if (testCase is PtcCase.AlreadySeen or PtcCase.SeenForAnotherValidator)
        {
            // Another signature, so a drop below is the pair rule and not the message id.
            PayloadAttestationMessage seen = PtcVote(PtcBlockRoot, PtcSlot, testCase == PtcCase.AlreadySeen ? PtcValidator : PtcValidator + 1, signatureSeed: 1);
            Handle(router, seen);
            router.MarkPayloadAttestationVerified(seen);
            received = 0;
        }

        byte[] payload = PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, dataSlot));
        if (testCase == PtcCase.Oversized)
            payload = [.. payload, 0];

        MessageValidity validity = router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(payload));

        AssertVerdict(router, validity, received, reason, rejected);
    }

    // altair is_current_slot: the slot's range widened by MAXIMUM_GOSSIP_CLOCK_DISPARITY at both ends, inclusive.
    [TestCase(-1, 500L, true, TestName = "previous-slot vote at the disparity after the slot start is consumed")]
    [TestCase(-1, 501L, false, TestName = "previous-slot vote past the disparity after the slot start")]
    [TestCase(1, 11500L, true, TestName = "next-slot vote at the disparity before its slot is consumed")]
    [TestCase(1, 11499L, false, TestName = "next-slot vote earlier than the disparity before its slot")]
    public void Payload_attestation_current_slot_allows_the_clock_disparity(int slotOffset, long millisecondsIntoWallSlot, bool consumed)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoWallSlot);
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => received++;
        ulong dataSlot = (ulong)((long)PtcSlot + slotOffset);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(dataSlot)));

        MessageValidity validity = router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, dataSlot))));

        AssertVerdict(router, validity, received, consumed ? null : slotOffset < 0 ? GossipDropReason.StaleSlot : GossipDropReason.FutureSlot, rejected: false);
    }

    // A slot from the wire near ulong.MaxValue must not wrap the slot-end time into the past.
    [Test]
    public void Payload_attestation_for_the_last_representable_slot_is_from_the_future()
    {
        (GossipRouter router, _) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);

        MessageValidity validity = router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, ulong.MaxValue))));

        AssertVerdict(router, validity, 0, GossipDropReason.FutureSlot, rejected: false);
    }

    /// <summary>A vote for the slot of a held block costs a store decode on the network thread, so the reads are bounded per slot.</summary>
    [Test]
    public void Voted_block_slot_reads_past_their_budget_skip_the_payload_attestation_slot_rule()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => received++;
        MessageValidity[] verdicts = new MessageValidity[GossipRouter.PtcBlockSlotReadsPerSlot + 1];
        for (int i = 0; i < verdicts.Length; i++)
        {
            Hash256 root = Keccak.Compute($"ptc block at another slot {i}");
            store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot - 1)));
            verdicts[i] = router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(root, PtcSlot))));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(router.GetDropCount(GossipDropReason.InvalidField), Is.EqualTo(GossipRouter.PtcBlockSlotReadsPerSlot), "within the budget");
            Assert.That(received, Is.EqualTo(1), "past the budget the vote is consumed for fork choice, which checks the slot again");
            Assert.That(verdicts, Is.All.EqualTo(MessageValidity.Ignored));
        }
    }

    /// <summary>Each raised vote costs fork choice a BLS verify, so a pair is raised at most the attempt limit of times while none has verified.</summary>
    [Test]
    public void Payload_attestation_repeats_with_other_signatures_are_raised_up_to_the_attempt_limit()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => received++;
        const int repeats = 10;

        for (byte seed = 0; seed <= repeats; seed++)
        {
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true,
                Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: seed))));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts));
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(repeats + 1 - GossipRouter.PayloadAttestationVerifyAttempts));
        }
    }

    /// <summary>gloas/p2p-interface.md IGNOREs a repeat only after the first valid message: a forgery that failed its verify must not hide the genuine vote.</summary>
    [Test]
    public void Payload_attestation_after_failed_verifies_is_raised_until_one_verifies([Values(0, 1, 2)] int forgeriesFirst)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        List<byte> raised = [];
        router.PayloadAttestationMessageReceived += (vote, _) => raised.Add(vote.Signature.Bytes[0]);
        PayloadAttestationMessage genuine = PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: 100);

        for (byte forged = 1; forged <= forgeriesFirst; forged++)
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: forged));
        Handle(router, genuine);
        router.MarkPayloadAttestationVerified(genuine);
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: 101));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raised, Is.EqualTo(Enumerable.Range(1, forgeriesFirst).Select(static i => (byte)i).Append((byte)100)), "the genuine vote is raised after the forgeries");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1), "a vote after the verified one is dropped");
        }
    }

    /// <summary>A vote the vote queue refused must give back its attempt and its message id, or a later copy is dropped unverified.</summary>
    [Test]
    public void Released_payload_attestation_gives_back_its_attempt_and_message_id()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => received++;
        PayloadAttestationMessage refused = PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: 1);
        Handle(router, refused);
        for (byte seed = 2; seed <= GossipRouter.PayloadAttestationVerifyAttempts; seed++)
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: seed));
        int beforeRelease = received;
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: 200));
        long droppedBeforeRelease = router.GetDropCount(GossipDropReason.Duplicate);

        router.ReleasePayloadAttestation(refused);
        Handle(router, refused);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((beforeRelease, droppedBeforeRelease), Is.EqualTo((GossipRouter.PayloadAttestationVerifyAttempts, 1L)), "fixture: the attempts were spent");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(droppedBeforeRelease), "the identical copy is not dropped");
            Assert.That(received, Is.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts + 1), "the identical copy is raised again");
        }
    }

    /// <summary>Copies of one message racing must spend one attempt: the losers of the message-id race hand theirs back.</summary>
    [Test]
    public void Identical_payload_attestations_raced_by_concurrent_handlers_spend_one_attempt([Range(0, 4)] int round)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => Interlocked.Increment(ref received);
        const int handlers = 16;
        byte[] copy = Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: 1)));
        using Barrier start = new(handlers);

        Parallel.For(0, handlers, new ParallelOptions { MaxDegreeOfParallelism = handlers }, _ =>
        {
            start.SignalAndWait();
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, copy);
        });

        int afterCopies = received;
        for (byte seed = 2; seed < 2 + GossipRouter.PayloadAttestationVerifyAttempts - 1; seed++)
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: seed));

        Assert.That((afterCopies, received), Is.EqualTo((1, GossipRouter.PayloadAttestationVerifyAttempts)), "the losing copies left their attempts for other votes");
    }

    /// <summary>Handlers racing on one (slot, validator) pair with different signatures must raise at most the attempt limit: the count is atomic.</summary>
    [Test]
    public void Payload_attestation_pair_raced_by_concurrent_handlers_is_raised_up_to_the_attempt_limit([Range(0, 4)] int round)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => Interlocked.Increment(ref received);
        const int handlers = 16;
        byte[][] messages = [.. Enumerable.Range(0, handlers).Select(i => Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: (byte)(i + 1)))))];
        using Barrier start = new(handlers);

        Parallel.For(0, handlers, new ParallelOptions { MaxDegreeOfParallelism = handlers }, i =>
        {
            start.SignalAndWait();
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, messages[i]);
        });

        Assert.That(received, Is.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts));
    }

    /// <summary>Many fresh pairs raced at once: a check-then-act claim or a pair created twice lets a pair pass more than the attempt limit.</summary>
    [Test]
    [Repeat(3)]
    public void Payload_attestation_pairs_raced_across_many_validators_are_each_raised_up_to_the_attempt_limit()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        const int handlers = 16;
        const int pairs = 400;
        int[] raised = new int[pairs];
        router.PayloadAttestationMessageReceived += (vote, _) => Interlocked.Increment(ref raised[vote.ValidatorIndex]);
        byte[][][] messages = [.. Enumerable.Range(0, pairs).Select(v => Enumerable.Range(0, handlers)
            .Select(i => Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, (ulong)v, signatureSeed: (byte)(i + 1))))).ToArray())];
        using Barrier start = new(handlers);

        Task[] tasks = [.. Enumerable.Range(0, handlers).Select(i => Task.Factory.StartNew(() =>
        {
            for (int pair = 0; pair < pairs; pair++)
            {
                start.SignalAndWait();
                router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, messages[pair][i]);
            }
        }, TaskCreationOptions.LongRunning))];
        Task.WaitAll(tasks);

        Assert.That(raised, Is.All.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts));
    }

    /// <summary>The verified mark and the attempt count share one lock: a mark racing claims on a fresh pair must still close it.</summary>
    [Test]
    [Repeat(8)]
    public void Payload_attestation_verified_mark_racing_claims_closes_the_pair()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        const int handlers = 2;
        const int pairs = 1500;
        router.PayloadAttestationMessageReceived += (_, _) => { };
        byte[][][] messages = [.. Enumerable.Range(0, pairs).Select(v => Enumerable.Range(0, handlers)
            .Select(i => Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, (ulong)v, signatureSeed: (byte)(i + 1))))).ToArray())];
        using Barrier start = new(handlers + 1);

        Task[] tasks = [.. Enumerable.Range(0, handlers + 1).Select(i => Task.Factory.StartNew(() =>
        {
            for (int pair = 0; pair < pairs; pair++)
            {
                start.SignalAndWait();
                if (i == handlers)
                    router.MarkPayloadAttestationVerified(PtcVote(PtcBlockRoot, PtcSlot, (ulong)pair, signatureSeed: 250));
                else
                    router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, messages[pair][i]);
            }
        }, TaskCreationOptions.LongRunning))];
        Task.WaitAll(tasks);

        int reopened = 0;
        router.PayloadAttestationMessageReceived += (_, _) => reopened++;
        for (int pair = 0; pair < pairs; pair++)
        {
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, (ulong)pair, signatureSeed: 251));
        }

        Assert.That(reopened, Is.Zero, "every pair was marked verified");
    }

    /// <summary>
    /// Votes under distinct validator indices are cheap to send: a flood of them must not reopen a verified pair for more verifies,
    /// nor stop a validator's first vote from reaching fork choice (gloas/p2p-interface.md <c>payload_attestation_message</c>).
    /// </summary>
    [Test]
    public void Payload_attestation_flood_of_distinct_validators_neither_reopens_a_verified_pair_nor_blocks_a_new_one()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int raised = 0;
        router.PayloadAttestationMessageReceived += (_, _) => raised++;
        PayloadAttestationMessage genuine = PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, signatureSeed: 1);
        Handle(router, genuine);
        router.MarkPayloadAttestationVerified(genuine);
        raised = 0;

        const int flood = 3 * GossipRouter.PayloadAttestationPairsPerSlot;
        for (ulong index = 1000; index < 1000 + flood; index++)
        {
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, index, signatureSeed: 2));
        }

        int floodRaised = raised;
        raised = 0;
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, signatureSeed: 3));
        int verifiedRaised = raised;
        raised = 0;
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator + 1, signatureSeed: 3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(floodRaised, Is.EqualTo(flood), "fixture: every flooded vote was new");
            Assert.That(verifiedRaised, Is.Zero, "the verified pair is still closed");
            Assert.That(raised, Is.EqualTo(1), "a validator's first vote after the flood is still raised");
            Assert.That(router.IsPayloadAttestationVerified(genuine), Is.True);
        }
    }

    /// <summary>
    /// A pair that spent its verify attempts reopens only after <see cref="GossipRouter.PayloadAttestationPairsPerSlot"/> newer pairs push it out,
    /// which bounds the pairs a slot holds and makes each reopening cost a flood of that size.
    /// </summary>
    [TestCase(-1, 0)]
    [TestCase(0, 1)]
    public void Payload_attestation_pair_with_spent_attempts_reopens_only_after_a_full_cache_of_newer_pairs(int newerPairsPastCapacity, int expectedRaised)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int raised = 0;
        router.PayloadAttestationMessageReceived += (vote, _) => raised += vote.ValidatorIndex == PtcValidator ? 1 : 0;
        for (byte seed = 1; seed <= GossipRouter.PayloadAttestationVerifyAttempts; seed++)
        {
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, seed));
        }

        Assert.That(raised, Is.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts), "fixture: the pair spent its attempts");
        for (ulong index = 1000; index < 1000 + (ulong)(GossipRouter.PayloadAttestationPairsPerSlot + newerPairsPastCapacity); index++)
        {
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, index));
        }

        raised = 0;
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, signatureSeed: 100));

        Assert.That(raised, Is.EqualTo(expectedRaised));
    }

    /// <summary>
    /// gloas/p2p-interface.md REJECTs a vote whose validator is not in get_ptc(head state, slot): non-members are never tracked, so a flood of
    /// them cannot push a member's spent pair out of the cache and reopen it for more verifies.
    /// </summary>
    [Test]
    public void Payload_attestation_flood_of_non_members_is_rejected_and_never_reopens_a_spent_member_pair()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        router.RequiresPtc = true;
        router.SetPtc(PtcSlot, [PtcValidator]);
        int raised = 0;
        router.PayloadAttestationMessageReceived += (_, _) => raised++;
        for (byte seed = 1; seed <= GossipRouter.PayloadAttestationVerifyAttempts; seed++)
        {
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, seed));
        }

        Assert.That(raised, Is.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts), "fixture: the member spent its attempts");
        const int flood = 3 * GossipRouter.PayloadAttestationPairsPerSlot;
        MessageValidity[] verdicts = new MessageValidity[flood];
        for (int i = 0; i < flood; i++)
        {
            verdicts[i] = Handle(router, PtcVote(PtcBlockRoot, PtcSlot, 1000 + (ulong)i));
        }

        raised = 0;
        long duplicatesBefore = router.GetDropCount(GossipDropReason.Duplicate);
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, signatureSeed: 100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts, Is.All.EqualTo(MessageValidity.Rejected));
            Assert.That(raised, Is.Zero, "the spent pair stays spent");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(duplicatesBefore + 1));
        }
    }

    /// <summary>A member's repeats are raised up to the attempt limit and ignored after a vote verified, as for any pair.</summary>
    [Test]
    public void Payload_attestation_member_repeats_are_ignored_after_the_attempts_and_after_a_verified_vote()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        router.RequiresPtc = true;
        router.SetPtc(PtcSlot, [PtcValidator, PtcValidator + 1]);
        List<ulong> raised = [];
        router.PayloadAttestationMessageReceived += (vote, _) => raised.Add(vote.ValidatorIndex);

        for (byte seed = 1; seed <= GossipRouter.PayloadAttestationVerifyAttempts + 2; seed++)
        {
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, seed));
        }

        PayloadAttestationMessage verified = PtcVote(PtcBlockRoot, PtcSlot, PtcValidator + 1, signatureSeed: 1);
        Handle(router, verified);
        router.MarkPayloadAttestationVerified(verified);
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator + 1, signatureSeed: 2));

        Assert.That(raised, Is.EqualTo(Enumerable.Repeat(PtcValidator, GossipRouter.PayloadAttestationVerifyAttempts).Append(PtcValidator + 1)));
    }

    /// <summary>gloas/p2p-interface.md needs the head state's committee: while it is unknown the vote is IGNOREd, nothing is tracked, and the vote is raised once the committee is known.</summary>
    [Test]
    public void Payload_attestation_with_an_unknown_ptc_is_ignored_without_tracking_the_pair()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        router.RequiresPtc = true;
        int raised = 0;
        router.PayloadAttestationMessageReceived += (_, _) => raised++;

        MessageValidity[] unknown = [.. Enumerable.Range(1, GossipRouter.PayloadAttestationVerifyAttempts + 1).Select(seed => Handle(router, PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: (byte)seed)))];
        int whileUnknown = raised;
        router.SetPtc(PtcSlot, [PtcValidator]);
        for (byte seed = 10; seed < 10 + GossipRouter.PayloadAttestationVerifyAttempts; seed++)
        {
            Handle(router, PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: seed));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unknown, Is.All.EqualTo(MessageValidity.Ignored));
            Assert.That(whileUnknown, Is.Zero);
            Assert.That(raised, Is.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts), "the pair kept its full attempts");
        }
    }

    /// <summary>A head that can no longer tell the committee (null) is unknown, not empty: an honest member is IGNOREd, not REJECTed, and nothing is tracked.</summary>
    [Test]
    public void Payload_attestation_after_the_ptc_becomes_unknown_is_ignored_not_rejected()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        router.RequiresPtc = true;
        int raised = 0;
        router.PayloadAttestationMessageReceived += (_, _) => raised++;
        router.SetPtc(PtcSlot, [PtcValidator]);

        router.SetPtc(PtcSlot, null);
        MessageValidity whileUnknown = Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, signatureSeed: 1));
        int raisedWhileUnknown = raised;
        router.SetPtc(PtcSlot, [PtcValidator]);
        Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, signatureSeed: 2));

        Assert.That((whileUnknown, raisedWhileUnknown, raised), Is.EqualTo((MessageValidity.Ignored, 0, 1)));
    }

    /// <summary>A head that changes its committee is followed: a validator that left the PTC is rejected, one that joined is raised.</summary>
    [Test]
    public void Payload_attestation_membership_follows_the_latest_ptc_of_the_slot()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        router.RequiresPtc = true;
        int raised = 0;
        router.PayloadAttestationMessageReceived += (_, _) => raised++;
        router.SetPtc(PtcSlot, [PtcValidator]);

        router.SetPtc(PtcSlot, [PtcValidator + 1]);
        MessageValidity leaver = Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator, signatureSeed: 1));
        MessageValidity joiner = Handle(router, PtcVote(PtcBlockRoot, PtcSlot, PtcValidator + 1, signatureSeed: 1));

        Assert.That((leaver, joiner, raised), Is.EqualTo((MessageValidity.Rejected, MessageValidity.Ignored, 1)));
    }

    /// <summary>Pairs of a slot too old to receive votes are dropped, so the tracked pairs stay bounded as slots advance.</summary>
    [Test]
    public void Payload_attestation_pairs_of_slots_that_can_no_longer_be_voted_are_dropped()
    {
        ManualTimestamper timestamper = new(SepoliaSlotStart(PtcSlot).AddMilliseconds(9000));
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, timestamper), LimboLogs.Instance);
        PayloadAttestationMessage old = PtcVote(PtcBlockRoot, PtcSlot);
        router.MarkPayloadAttestationVerified(old);
        Assert.That(router.IsPayloadAttestationVerified(old), Is.True, "fixture: the pair is tracked");

        timestamper.Add(TimeSpan.FromSeconds(3 * Sepolia.SecondsPerSlot));
        router.MarkPayloadAttestationVerified(PtcVote(PtcBlockRoot, PtcSlot + 3));

        Assert.That(router.IsPayloadAttestationVerified(old), Is.False, "the slot is past the current-slot window and its pairs are gone");
    }

    /// <summary>A vote for a block not held yet spends none of the pair's verify attempts: the same member's vote is raised once the block is stored.</summary>
    [Test]
    public void Payload_attestation_for_an_unheld_block_does_not_claim_the_pair()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        int received = 0;
        router.PayloadAttestationMessageReceived += (_, _) => received++;

        router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: 1))));
        int beforeBlock = received;
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: 2))));

        Assert.That((beforeBlock, received), Is.EqualTo((0, 1)));
    }

    /// <summary>Distinct votes are cheap to send, so a flood of them must not evict the message ids of other topics and let their replays through.</summary>
    [Test]
    public void Payload_attestation_flood_keeps_the_message_ids_of_other_topics()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int slashings = 0;
        int votes = 0;
        router.AttesterSlashingReceived += (_, _) => slashings++;
        router.PayloadAttestationMessageReceived += (_, _) => votes++;
        byte[] slashing = GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.FuluSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 4));

        MessageValidity first = router.Handle(GossipTopics.AttesterSlashing, gloasTopic: false, slashing);
        for (ulong validator = 0; validator < 2 * GossipRouter.SeenCacheSize; validator++)
        {
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true,
                Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, validator))));
        }

        MessageValidity replay = router.Handle(GossipTopics.AttesterSlashing, gloasTopic: false, slashing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, replay), Is.EqualTo((MessageValidity.Ignored, MessageValidity.Ignored)));
            Assert.That(votes, Is.EqualTo(2 * GossipRouter.SeenCacheSize), "fixture: every vote was new");
            Assert.That(slashings, Is.EqualTo(1), "the replayed slashing is still known by its message id");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1));
        }
    }

    private static MessageValidity Handle(GossipRouter router, PayloadAttestationMessage vote) =>
        router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(vote)));

    private static PayloadAttestationMessage PtcVote(Hash256 blockRoot, ulong slot, ulong validator = PtcValidator, byte signatureSeed = 0) => new()
    {
        ValidatorIndex = validator,
        Data = new PayloadAttestationData { BeaconBlockRoot = blockRoot, Slot = slot, PayloadPresent = true, BlobDataAvailable = true },
        Signature = new BlsSignature([.. Enumerable.Repeat(signatureSeed, BlsSignature.Length)]),
    };

    private static (GossipRouter Router, BeaconChainStore Store) CreatePtcRouter(ulong wallSlot, long millisecondsIntoSlot)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        ManualTimestamper timestamper = new(SepoliaSlotStart(wallSlot).AddMilliseconds(millisecondsIntoSlot));
        return (new GossipRouter(Sepolia, new SlotClock(Sepolia, timestamper), LimboLogs.Instance, store), store);
    }
}
