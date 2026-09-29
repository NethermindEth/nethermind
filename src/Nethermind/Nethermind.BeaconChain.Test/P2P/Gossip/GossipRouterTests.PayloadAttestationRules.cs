// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    [TestCase(PtcCase.BlockNotHeld, GossipDropReason.InvalidField, false)]
    [TestCase(PtcCase.BlockAtAnotherSlot, GossipDropReason.InvalidField, false)]
    [TestCase(PtcCase.Oversized, GossipDropReason.Oversized, true)]
    public void Payload_attestation_message_rules_needing_no_state(PtcCase testCase, GossipDropReason? reason, bool rejected)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        int received = 0;
        router.PayloadAttestationMessageReceived += _ => received++;
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
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true,
                Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, testCase == PtcCase.AlreadySeen ? PtcValidator : PtcValidator + 1, signatureSeed: 1))));
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
        router.PayloadAttestationMessageReceived += _ => received++;
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
        router.PayloadAttestationMessageReceived += _ => received++;
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

    /// <summary>Each raised vote costs fork choice a BLS verify, so a repeat for the same (slot, validator) is dropped whatever its signature.</summary>
    [Test]
    public void Payload_attestation_repeats_with_other_signatures_are_raised_once()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int received = 0;
        router.PayloadAttestationMessageReceived += _ => received++;
        const int repeats = 10;

        for (byte seed = 0; seed <= repeats; seed++)
        {
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true,
                Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: seed))));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.EqualTo(1));
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(repeats));
        }
    }

    /// <summary>Handlers racing on one (slot, validator) pair with different signatures must raise a single vote: the pair claim is atomic.</summary>
    [Test]
    public void Payload_attestation_pair_raced_by_concurrent_handlers_is_raised_once([Range(0, 4)] int round)
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        store.PutForkedBlock(PtcBlockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(PtcSlot)));
        int received = 0;
        router.PayloadAttestationMessageReceived += _ => Interlocked.Increment(ref received);
        const int handlers = 16;
        byte[][] messages = [.. Enumerable.Range(0, handlers).Select(i => Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(PtcBlockRoot, PtcSlot, signatureSeed: (byte)(i + 1)))))];
        using Barrier start = new(handlers);

        Parallel.For(0, handlers, new ParallelOptions { MaxDegreeOfParallelism = handlers }, i =>
        {
            start.SignalAndWait();
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, messages[i]);
        });

        Assert.That(received, Is.EqualTo(1));
    }

    /// <summary>A vote for a block not held yet is not the pair's one verify: the same member's vote is raised once the block is stored.</summary>
    [Test]
    public void Payload_attestation_for_an_unheld_block_does_not_claim_the_pair()
    {
        (GossipRouter router, BeaconChainStore store) = CreatePtcRouter(PtcSlot, millisecondsIntoSlot: 9000);
        int received = 0;
        router.PayloadAttestationMessageReceived += _ => received++;

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
        router.AttesterSlashingReceived += _ => slashings++;
        router.PayloadAttestationMessageReceived += _ => votes++;
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
