// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// <see cref="BlockImporter"/> on Gloas blocks and their execution payload envelopes (specs/gloas/fork-choice.md
/// <c>on_block</c> and <c>on_execution_payload_envelope</c>), with genuinely signed blocks from a Fulu anchor.
/// </summary>
public class GloasBlockImporterTests
{
    private const ulong ForkSlot = 32;

    /// <summary>
    /// The first Gloas block is applied to the Fulu parent's post-state carried across the fork. That crossing
    /// mutates in place and <c>upgrade_to_gloas</c> aliases the Fulu arrays, so it must run on a copy, or the Fulu
    /// state fork choice still resolves for the anchor would silently change under it.
    /// </summary>
    [Test]
    public void First_gloas_block_crosses_the_fork_on_a_copy_and_is_stored_in_its_own_shape()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new();
        BeaconChainStore store = chain.CreateStore();
        BlockImporter importer = chain.CreateImporter(engine, store: store);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);

        BlockImportResult result = importer.Import(first.Forked, first.Root, verifySignatures: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
            Assert.That(importer.IsKnown(first.Root), Is.True);
            Assert.That(store.TryGetForkedBlock(first.Root, out ForkedSignedBeaconBlock? stored) ? stored : null, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>());
            Assert.That(SszRoots.HashTreeRoot(chain.AnchorState), Is.EqualTo(chain.AnchorBlock.Message!.StateRoot), "the Fulu lineage state is untouched by the crossing");
            Assert.That(engine.HasAnsweredNewPayload, Is.False, "a Gloas block carries only a bid; its payload reaches the engine in the envelope");
        }
    }

    [Test]
    public void Block_whose_shape_is_not_the_fork_of_its_slot_is_invalid_before_any_state_is_touched()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = chain.CreateImporter(engine);
        SignedBeaconBlock fuluShaped = new() { Message = new BeaconBlock { Slot = ForkSlot, ParentRoot = chain.AnchorRoot }, Signature = default };

        BlockImportResult result = importer.Import(new ForkedSignedBeaconBlock.OfFulu(fuluShaped), Hash(0x5A), verifySignatures: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
            Assert.That(engine.HasAnsweredNewPayload, Is.False);
        }
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>on_block</c>: a block that builds on its parent's full payload needs that
    /// payload verified. Before the envelope it must be a retriable deferral that records nothing, never Invalid,
    /// or a child racing its parent's envelope is dropped for good. An empty child never waits.
    /// </summary>
    [Test]
    public void Child_waits_for_its_parents_envelope_only_when_it_builds_on_the_full_payload(
        [Values] bool full,
        [Values(ExecutionStatus.Valid, ExecutionStatus.Optimistic)] ExecutionStatus envelopeVerdict)
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new() { EnvelopeVerdict = envelopeVerdict };
        BeaconChainStore store = chain.CreateStore();
        BlockImporter importer = chain.CreateImporter(engine, store: store);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(first, ForkSlot + 1, full, 0xA2);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        BlockImportResult beforeEnvelope = importer.Import(child.Forked, child.Root, verifySignatures: true);
        bool storedBeforeEnvelope = store.HasBlock(child.Root);
        bool knownBeforeEnvelope = importer.IsKnown(child.Root);
        ExecutionPayloadEnvelopeImportResult envelope = importer.ImportEnvelope(first.Envelope);
        BlockImportResult afterEnvelope = full ? importer.Import(child.Forked, child.Root, verifySignatures: true) : BlockImportResult.AlreadyKnown;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeEnvelope, Is.EqualTo(full ? BlockImportResult.ParentPayloadUnverified : BlockImportResult.Imported));
            Assert.That(knownBeforeEnvelope, Is.EqualTo(!full), "a deferred child is not in fork choice");
            Assert.That(storedBeforeEnvelope, Is.EqualTo(!full), "a deferred child is not stored");
            Assert.That(envelope, Is.EqualTo(envelopeVerdict == ExecutionStatus.Valid ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.Optimistic));
            Assert.That(afterEnvelope, Is.EqualTo(full ? BlockImportResult.Imported : BlockImportResult.AlreadyKnown), "an optimistic payload is recorded as an optimistic block is imported");
        }
    }

    /// <summary>An envelope that is refused, or whose blob data is not held, records nothing, so the full child keeps waiting.</summary>
    [TestCase(ExecutionStatus.Invalid, true, ExecutionPayloadEnvelopeImportResult.Invalid)]
    [TestCase(ExecutionStatus.Valid, false, ExecutionPayloadEnvelopeImportResult.DataUnavailable)]
    public void Envelope_that_does_not_verify_leaves_the_full_child_waiting(ExecutionStatus envelopeVerdict, bool dataAvailable, ExecutionPayloadEnvelopeImportResult expected)
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new() { EnvelopeVerdict = envelopeVerdict };
        BlockImporter importer = chain.CreateImporter(engine, isEnvelopeDataAvailable: (_, _) => dataAvailable);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        ExecutionPayloadEnvelopeImportResult envelope = importer.ImportEnvelope(first.Envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(envelope, Is.EqualTo(expected));
            Assert.That(importer.Import(child.Forked, child.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified));
        }
    }

    /// <summary>A repeated envelope, and one for a block fork choice does not hold, are answered before any hashing or engine call.</summary>
    [Test]
    public void Envelope_already_verified_or_for_an_unknown_block_never_reaches_the_engine()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = chain.CreateImporter(engine);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block neverImported = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        ExecutionPayloadEnvelopeImportResult firstTime = importer.ImportEnvelope(first.Envelope);
        ExecutionPayloadEnvelopeImportResult secondTime = importer.ImportEnvelope(first.Envelope);
        ExecutionPayloadEnvelopeImportResult unknown = importer.ImportEnvelope(neverImported.Envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstTime, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(secondTime, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.AlreadyKnown));
            Assert.That(unknown, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
            Assert.That(engine.EnvelopeCalls, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>notify_forkchoice_updated</c>: a Gloas head maps to its bid's <c>parent_block_hash</c>
    /// until its payload is verified, since the execution layer has no other payload of it. A VALID envelope does not
    /// make the block execution-valid: that verdict could not be undone before fork choice splits payload status.
    /// </summary>
    [Test]
    public void Head_execution_hash_moves_to_the_bid_block_hash_once_the_envelope_verifies_but_stays_optimistic()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new();
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = chain.CreateImporter(engine, snapshots: snapshots);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        HeadView beforeEnvelope = importer.ComputeHead();
        importer.ImportEnvelope(first.Envelope);
        HeadView afterEnvelope = importer.ComputeHead();
        Hash256 anchorPayloadHash = chain.AnchorBlock.Message!.Body!.ExecutionPayload!.BlockHash!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeEnvelope.HeadRoot, Is.EqualTo(first.Root));
            Assert.That(beforeEnvelope.HeadExecutionHash, Is.EqualTo(first.Bid.ParentBlockHash));
            Assert.That(afterEnvelope.HeadExecutionHash, Is.EqualTo(first.Bid.BlockHash));
            Assert.That(afterEnvelope.FinalizedExecutionHash, Is.EqualTo(anchorPayloadHash), "a Fulu checkpoint keeps its own payload hash");
            Assert.That(snapshots.Current!.Nodes.Single(n => n.Root == first.Root).ExecutionStatus, Is.EqualTo(ExecutionStatus.Optimistic));
        }
    }

    /// <summary>The safe hash for a justified Gloas block is its bid's <c>parent_block_hash</c> (gloas/fast-confirmation.md <c>get_safe_execution_block_hash</c>).</summary>
    [Test]
    public void Justified_gloas_checkpoint_maps_to_its_bid_parent_block_hash()
    {
        ForkCrossingChain fork = ForkCrossingChain.Instance;
        SignedGloasChain chain = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        foreach (ForkCrossingChain.ChainBlock block in (ForkCrossingChain.ChainBlock[])[fork.First, .. fork.Voting])
        {
            Assert.That(importer.Import(new ForkedSignedBeaconBlock.OfGloas(block.Block), block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        }

        importer.OnSlotTick(3 * chain.Spec.SlotsPerEpoch);
        HeadView head = importer.ComputeHead();
        ExecutionPayloadBid firstBid = fork.First.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(head.Justified.Root, Is.EqualTo(fork.First.Root), "fixture: the epoch-3 pull-up justifies the first Gloas block");
            Assert.That(head.JustifiedExecutionHash, Is.EqualTo(firstBid.ParentBlockHash));
            Assert.That(head.JustifiedExecutionHash, Is.Not.EqualTo(firstBid.BlockHash));
        }
    }

    /// <summary>
    /// Envelopes are not persisted, so a restart replays stored blocks with no payload recorded. A stored child that
    /// builds full on its parent passed the gate before it was stored, which proves that payload was verified.
    /// </summary>
    [Test]
    public void Replayed_full_child_stands_in_for_its_parents_envelope()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = chain.CreateImporter(engine);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        SignedGloasChain.Block grandchild = chain.Next(child, ForkSlot + 2, full: true, 0xA3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.Import(first.Forked, first.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
            Assert.That(importer.Import(child.Forked, child.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
            Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.AlreadyKnown), "the replayed child recorded its parent's payload");
            Assert.That(importer.Import(grandchild.Forked, grandchild.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "the replayed tip's own payload is still unverified");
            Assert.That(engine.EnvelopeCalls, Is.Zero);
        }
    }

    /// <summary>
    /// Fork choice resolves checkpoint states by root, possibly epochs after the block, and the per-block Gloas tier
    /// holds only the last two epochs of blocks. The first block of an epoch, and the parent of a block that skipped an
    /// epoch's first slot, are checkpoint blocks and must outlive that tier; a block that is neither must not linger.
    /// </summary>
    [Test]
    public void Checkpoint_block_states_outlive_the_per_block_tier()
    {
        SignedGloasChain chain = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block epochStart = chain.Next(null, ForkSlot, full: false, 0xB0);
        SignedGloasChain.Block middle = chain.Next(epochStart, ForkSlot + 1, full: false, 0xB1);
        SignedGloasChain.Block lastBeforeSkip = chain.Next(middle, ForkSlot + 2, full: false, 0xB2);
        Import(importer, epochStart, middle, lastBeforeSkip);

        // Skips slot 64; the 64 blocks after slot 65 push every older state out of the per-block tier.
        SignedGloasChain.Block tip = lastBeforeSkip;
        for (ulong slot = 2 * ForkSlot + 1; slot <= 4 * ForkSlot + 1; slot++)
        {
            tip = chain.Next(tip, slot, full: false, (byte)(slot + 0x60));
            Import(importer, tip);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.ImportEnvelope(epochStart.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "an epoch's first block");
            Assert.That(importer.ImportEnvelope(lastBeforeSkip.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "the checkpoint block of the epoch whose first slot was skipped");
            Assert.That(importer.ImportEnvelope(middle.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock), "a block that is no checkpoint ages out");
        }
    }

    /// <summary>
    /// The production importer checks envelopes with the Gloas custody-sampling rule, which fails closed while the
    /// node's identity is unknown; a permissive stub here would admit a payload whose blobs nobody holds.
    /// </summary>
    [Test]
    public void Factory_importer_refuses_an_envelope_whose_blobs_it_cannot_sample()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new();
        PubkeyCache pubkeys = new();
        pubkeys.Build(chain.AnchorState.Validators!);
        BlockImporterFactory factory = new(chain.Spec, chain.CreateStore(), pubkeys, engine, new BeaconChainConfig(), LimboLogs.Instance, new DataColumnSidecarPool(),
            clock: new SlotClock(chain.Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + (ForkSlot + 1) * chain.Spec.SecondsPerSlot))));
        IBlockImporter importer = factory.Create(chain.AnchorState, chain.AnchorBlock, chain.AnchorRoot);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1, blobCommitments: [default]);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.DataUnavailable));
            Assert.That(engine.EnvelopeCalls, Is.Zero);
        }
    }

    /// <summary>
    /// The gossip proposer check reads a Gloas block's proposer from its parent's lookahead, not from the Fulu lineage
    /// frozen at the fork. A child in the parent's next epoch reads the lookahead's second half; reading the first
    /// half would refuse the real proposer of every block that opens an epoch.
    /// </summary>
    [Test]
    public void Gloas_proposer_is_checked_against_the_parents_lookahead([Values] bool nextEpoch)
    {
        SignedGloasChain chain = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        ulong[] lookahead = first.PostState.ProposerLookahead!;
        int offsetInEpoch = nextEpoch ? Enumerable.Range(0, (int)ForkSlot).First(i => lookahead[i] != lookahead[ForkSlot + (ulong)i]) : 1;
        SignedGloasChain.Block child = chain.Next(first, (nextEpoch ? 2 * ForkSlot : ForkSlot) + (ulong)offsetInEpoch, full: false, 0xA2);
        Import(importer, first);

        bool expected = importer.IsExpectedProposer(child.Forked);
        child.Signed.Message!.ProposerIndex = (child.Signed.Message.ProposerIndex + 1) % ValidatorCount;
        bool otherProposer = importer.IsExpectedProposer(child.Forked);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected, Is.True);
            Assert.That(otherProposer, Is.False);
        }
    }

    public enum Forgery
    {
        None,
        BodyAltered,
        OtherProposer,
        OtherProposerSigned,
        ProposerPastRegistry,
    }

    /// <summary>
    /// A deferred block waits unchecked in a bounded retry set and marks its (slot, proposer) seen, so one its expected
    /// proposer did not sign must be refused before it is deferred, or forged children of the head could crowd out the real one.
    /// Past the parent's lookahead window the expected proposer comes from the parent's post-state advanced to the child's slot.
    /// </summary>
    [TestCase(Forgery.None, BlockImportResult.ParentPayloadUnverified, false)]
    [TestCase(Forgery.BodyAltered, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.OtherProposer, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.OtherProposerSigned, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.ProposerPastRegistry, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.None, BlockImportResult.ParentPayloadUnverified, true)]
    [TestCase(Forgery.BodyAltered, BlockImportResult.Invalid, true)]
    [TestCase(Forgery.OtherProposer, BlockImportResult.Invalid, true)]
    [TestCase(Forgery.OtherProposerSigned, BlockImportResult.Invalid, true)]
    [TestCase(Forgery.ProposerPastRegistry, BlockImportResult.Invalid, true)]
    public void Full_child_is_deferred_only_when_its_expected_proposer_signed_it(Forgery forgery, BlockImportResult expected, bool pastLookahead)
    {
        SignedGloasChain chain = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(first, pastLookahead ? 3 * ForkSlot : ForkSlot + 1, full: true, 0xA2);
        Import(importer, first);
        BeaconBlockGloas message = child.Signed.Message!;
        switch (forgery)
        {
            case Forgery.BodyAltered:
                message.Body!.Graffiti = Hash(0x66);
                break;
            case Forgery.OtherProposer:
                message.ProposerIndex = (message.ProposerIndex + 1) % ValidatorCount;
                break;
            case Forgery.OtherProposerSigned:
                // Validly signed by the validator it names, so only the lookahead can tell it is not the expected proposer.
                message.ProposerIndex = (message.ProposerIndex + 1) % ValidatorCount;
                Hash256 proposerDomain = first.PostState.GetDomain(DomainType.BeaconProposer, first.PostState.GetCurrentEpoch());
                child.Signed.Signature = Sign(ValidatorKey((int)message.ProposerIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message), proposerDomain));
                break;
            case Forgery.ProposerPastRegistry:
                message.ProposerIndex = ulong.MaxValue;
                break;
        }

        Assert.That(importer.Import(child.Forked, SszRoots.HashTreeRoot(message), verifySignatures: true), Is.EqualTo(expected));
    }

    /// <summary>
    /// A valid full child past its parent's lookahead window waits for the parent's envelope like any other: refusing it
    /// would penalize its sender and drop a block the node needs once the payload is verified.
    /// </summary>
    [Test]
    public void Full_child_past_its_parents_lookahead_is_deferred_until_the_envelope()
    {
        SignedGloasChain chain = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(first, 3 * ForkSlot, full: true, 0xA2);
        Import(importer, first);

        BlockImportResult beforeEnvelope = importer.Import(child.Forked, child.Root, verifySignatures: true);
        ExecutionPayloadEnvelopeImportResult envelope = importer.ImportEnvelope(first.Envelope);
        BlockImportResult afterEnvelope = importer.Import(child.Forked, child.Root, verifySignatures: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeEnvelope, Is.EqualTo(BlockImportResult.ParentPayloadUnverified));
            Assert.That(envelope, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(afterEnvelope, Is.EqualTo(BlockImportResult.Imported));
        }
    }

    public enum OnBlockAssertion
    {
        CurrentSlot,
        FarFutureSlot,
        AfterFinalizedSlot,
        DescendsFromFinalized,
        AfterParentSlot,
    }

    /// <summary>
    /// specs/phase0/fork-choice.md and specs/gloas/fork-choice.md <c>on_block</c> assert these before <c>state_transition</c>,
    /// whose <c>process_slots</c> is linear in the slot distance to the parent: a peer's block at slot 2^40 on a known parent
    /// would keep the import worker busy for good. The current slot is the node's clock: fork-choice time is ticked to the
    /// block's own slot on import, which would never refuse a block from the future.
    /// </summary>
    [Test]
    public void Block_failing_an_on_block_assertion_is_refused_before_its_state_transition([Values] OnBlockAssertion assertion)
    {
        SignedGloasChain chain = new();
        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        SlotClock? clock = assertion is OnBlockAssertion.CurrentSlot ? ClockAt(chain, ForkSlot + 1, millisecondsEarly: GossipRouter.MaximumGossipClockDisparityMs + 1) : null;
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(logger)), clock: clock);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);
        SignedGloasChain.Block block;
        switch (assertion)
        {
            case OnBlockAssertion.CurrentSlot:
                block = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                break;
            case OnBlockAssertion.FarFutureSlot:
                block = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                block.Signed.Message!.Slot = 1UL << 40;
                break;
            case OnBlockAssertion.AfterParentSlot:
                block = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                block.Signed.Message!.Slot = ForkSlot;
                break;
            case OnBlockAssertion.AfterFinalizedSlot:
                // The checkpoint block is at slot 33 and the child sits on the bound, slot 64; full, so a missed bound would park it on the unverified payload.
                SignedGloasChain.Block finalized = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                Import(importer, finalized);
                Finalize(importer, new CheckpointRef(2, finalized.Root));
                block = chain.Next(finalized, 2 * ForkSlot, full: true, 0xA5);
                break;
            default:
                // Finalizes the epoch-2 checkpoint block of one branch; the other branch forked off before it.
                SignedGloasChain.Block finalizedBranch = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                SignedGloasChain.Block otherBranch = chain.Next(first, ForkSlot + 2, full: false, 0xA3);
                SignedGloasChain.Block checkpoint = chain.Next(finalizedBranch, 2 * ForkSlot, full: false, 0xA4);
                Import(importer, finalizedBranch, otherBranch, checkpoint);
                Finalize(importer, new CheckpointRef(2, checkpoint.Root));
                block = chain.Next(otherBranch, 2 * ForkSlot + 1, full: false, 0xA5);
                break;
        }

        Hash256 root = SszRoots.HashTreeRoot(block.Signed.Message!);
        logger.LogList.Clear();

        BlockImportResult result = ImportOrFailIfStuck(importer, block.Forked, root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
            Assert.That(logger.LogList, Has.One.Contains("before its state transition"));
            Assert.That(importer.IsKnown(root), Is.False);
        }
    }

    /// <summary>
    /// Runs one import, failing the test instead of hanging it when the importer is stuck in <c>process_slots</c>; the
    /// bound only stops a regression, the caller asserts which check refused the block.
    /// </summary>
    internal static BlockImportResult ImportOrFailIfStuck(IBlockImporter importer, ForkedSignedBeaconBlock block, Hash256 root)
    {
        Task<BlockImportResult> import = Task.Run(() => importer.Import(block, root, verifySignatures: true));
        Assert.That(import.Wait(TimeSpan.FromMinutes(1)), Is.True, "the import is still running the state transition toward the block's slot");
        return import.Result;
    }

    /// <summary>A clock stopped <paramref name="millisecondsEarly"/> before <paramref name="slot"/> starts.</summary>
    private static SlotClock ClockAt(SignedGloasChain chain, ulong slot, long millisecondsEarly) =>
        new(chain.Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeMilliseconds((long)(chain.Spec.GenesisTime + slot * chain.Spec.SecondsPerSlot) * 1000 - millisecondsEarly).UtcDateTime));

    /// <summary>Stands in for a finalization no short fixture chain reaches: the justification it needs takes epochs of votes.</summary>
    private static void Finalize(BlockImporter importer, CheckpointRef finalized)
    {
        object runner = typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        ForkChoiceStore store = (ForkChoiceStore)typeof(ForkChoiceRunner).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;
        store.UpdateCheckpoints(finalized, finalized);
    }

    /// <summary>
    /// The Gloas tier stands in for the spec's <c>store.block_states</c>, which only <c>on_block</c> writes after every
    /// assertion passed. A block fork choice refused must not take a slot in that bounded tier or be resolvable from it,
    /// nor be stored, where by-root requests and restart replay would serve it as imported.
    /// </summary>
    [Test]
    public void Block_refused_by_fork_choice_leaves_no_state_behind()
    {
        SignedGloasChain chain = new();
        BeaconChainStore store = chain.CreateStore();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), store: store);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        SignedGloasChain.Block grandchild = chain.Next(child, ForkSlot + 2, full: false, 0xA3);
        Import(importer, first);
        importer.OnInvalidExecutionPayload(first.Root, latestValidHash: null);

        BlockImportResult result = importer.Import(child.Forked, child.Root, verifySignatures: true);
        grandchild.Signed.Message!.ProposerIndex = (grandchild.Signed.Message.ProposerIndex + 1) % ValidatorCount;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "fixture: fork choice refuses a child of an invalid payload");
            Assert.That(importer.IsExpectedProposer(grandchild.Forked), Is.True, "no lookahead is held for the refused block, so the check defers");
            Assert.That(store.HasBlock(child.Root), Is.False, "the refused block is not stored");
            Assert.That(importer.IsKnown(child.Root), Is.False);
        }
    }

    /// <summary>
    /// The Gloas tier can outlive a root finalization pruned from fork choice, and recording that root's payload would
    /// throw. The spec asserts the root is in <c>store.block_states</c> before any verification, so the envelope is
    /// answered unknown without an engine call.
    /// </summary>
    [Test]
    public void Envelope_for_a_block_fork_choice_no_longer_holds_never_reaches_the_engine()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = chain.CreateImporter(engine);
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block pruned = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        Import(importer, first);
        // Stands in for finalization pruning: that needs a finalized block past the proto-array's 256-node prune threshold.
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        states.RetainGloas(pruned.Root, pruned.PostState);

        ExecutionPayloadEnvelopeImportResult result = importer.ImportEnvelope(pruned.Envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
            Assert.That(engine.EnvelopeCalls, Is.Zero);
        }
    }

    /// <summary>
    /// The per-branch epoch memo refuses a state whose epoch boundary it was not built on. A block extending a branch
    /// other than the last imported block's, in an epoch both branches entered from different boundary blocks, must
    /// get its own memo or the valid block is refused.
    /// </summary>
    [Test]
    public void Block_on_a_sibling_branch_across_an_epoch_boundary_imports()
    {
        SignedGloasChain chain = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block left = chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        SignedGloasChain.Block right = chain.Next(first, ForkSlot + 2, full: false, 0xA3);
        SignedGloasChain.Block leftNextEpoch = chain.Next(left, 2 * ForkSlot, full: false, 0xA4);
        SignedGloasChain.Block rightNextEpoch = chain.Next(right, 2 * ForkSlot + 1, full: false, 0xA5);
        SignedGloasChain.Block leftTip = chain.Next(leftNextEpoch, 2 * ForkSlot + 2, full: false, 0xA6);
        Import(importer, first, left, right, leftNextEpoch, rightNextEpoch);

        Assert.That(importer.Import(leftTip.Forked, leftTip.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
    }

    /// <summary>A Gloas head has no Fulu lineage to adopt; treating it as a reorg to an unretained state would warn on every head step.</summary>
    [Test]
    public void Gloas_head_step_is_not_taken_for_a_reorg()
    {
        SignedGloasChain chain = new();
        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(logger)));
        SignedGloasChain.Block first = chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);

        HeadView head = importer.ComputeHead();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(head.HeadRoot, Is.EqualTo(first.Root), "fixture: the Gloas block is the head");
            Assert.That(logger.LogList, Is.Empty);
        }
    }

    private static void Import(BlockImporter importer, params SignedGloasChain.Block[] blocks)
    {
        foreach (SignedGloasChain.Block block in blocks)
        {
            Assert.That(importer.Import(block.Forked, block.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported), $"fixture: block at slot {block.Signed.Message!.Slot}");
        }
    }
}
