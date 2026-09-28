// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// A node started from a finalized Gloas checkpoint (specs/gloas/fork-choice.md <c>get_forkchoice_store</c>, with
/// <c>payloads = {}</c>): the importer the factory builds on it, the anchor state it keeps, and the finalized Gloas
/// state it persists for the next start.
/// </summary>
public class GloasAnchorImportTests
{
    private const ulong ForkSlot = 32;

    /// <summary>
    /// The anchor's payload is not known verified, so a child that builds on it full waits for the anchor's envelope,
    /// which verifies against the anchor state; a child that builds on it empty imports at once.
    /// </summary>
    [Test]
    public void Factory_importer_on_a_gloas_anchor_imports_an_empty_child_and_defers_a_full_child_until_the_anchor_envelope([Values] bool full)
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(anchor, ForkSlot + 1, full, 0xA2);
        IBlockImporter importer = CreateFactoryImporter(chain, anchor, new SignedGloasChain.EnvelopeEngine(), chain.CreateStore());

        BlockImportResult first = importer.Import(child.Forked, child.Root, verifySignatures: true);
        ExecutionPayloadEnvelopeImportResult envelope = importer.ImportEnvelope(anchor.Envelope);
        BlockImportResult retry = importer.Import(child.Forked, child.Root, verifySignatures: true);
        ExecutionPayloadEnvelopeImportResult childEnvelope = importer.ImportEnvelope(child.Envelope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(full ? BlockImportResult.ParentPayloadUnverified : BlockImportResult.Imported));
            Assert.That(envelope, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "the anchor envelope verifies against the anchor state");
            Assert.That(retry, Is.EqualTo(full ? BlockImportResult.Imported : BlockImportResult.AlreadyKnown));
            Assert.That(childEnvelope, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "the child's own envelope verifies against the child's post-state");
        }
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>notify_forkchoice_updated</c>: the finalized hash of a Gloas checkpoint is its bid's
    /// <c>parent_block_hash</c>. The anchor's own <c>block_hash</c> need not exist on the execution layer until its envelope
    /// is verified, so the head moves to it only then.
    /// </summary>
    [Test]
    public void Gloas_anchor_head_and_checkpoints_map_to_its_bid_parent_block_hash_until_its_envelope_verifies()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        IBlockImporter importer = CreateFactoryImporter(chain, anchor, new SignedGloasChain.EnvelopeEngine(), chain.CreateStore());

        HeadView before = importer.ComputeHead();
        importer.ImportEnvelope(anchor.Envelope);
        HeadView after = importer.ComputeHead();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.Bid.ParentBlockHash, Is.Not.EqualTo(anchor.Bid.BlockHash), "fixture: the anchor builds on an empty payload");
            Assert.That(before.HeadRoot, Is.EqualTo(anchor.Root));
            Assert.That(before.HeadExecutionHash, Is.EqualTo(anchor.Bid.ParentBlockHash));
            Assert.That(before.JustifiedExecutionHash, Is.EqualTo(anchor.Bid.ParentBlockHash));
            Assert.That(before.FinalizedExecutionHash, Is.EqualTo(anchor.Bid.ParentBlockHash));
            Assert.That(after.HeadExecutionHash, Is.EqualTo(anchor.Bid.BlockHash));
            Assert.That(after.FinalizedExecutionHash, Is.EqualTo(anchor.Bid.ParentBlockHash));
        }
    }

    /// <summary>
    /// The anchor is the justified and finalized checkpoint, so fork choice resolves its state for as long as no later
    /// checkpoint is finalized, including after more epoch boundaries than the boundary tier holds. No import retains
    /// the anchor state; only the importer's pin holds it. Once a held block's state has aged out of every tier, a
    /// persisted copy serves it; a persisted state of a block fork choice does not hold costs no store read.
    /// </summary>
    [Test]
    public void Gloas_anchor_state_outlives_more_epoch_boundaries_than_the_boundary_tier_holds()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        BeaconChainStore store = chain.CreateStore();
        IBlockImporter importer = CreateFactoryImporter(chain, anchor, new SignedGloasChain.EnvelopeEngine(), store);
        SignedGloasChain.Block unheld = chain.Next(anchor, ForkSlot + 1, full: false, 0xB0);

        // Two epochs of blocks push any retention out of the per-block tier, then nine epoch starts out of the boundary tier.
        ulong[] slots = [.. Enumerable.Range((int)ForkSlot + 1, 2 * (int)ForkSlot).Select(static s => (ulong)s), .. Enumerable.Range(4, 9).Select(static e => (ulong)e * ForkSlot)];
        SignedGloasChain.Block tip = anchor;
        SignedGloasChain.Block? held = null;
        foreach (ulong slot in slots)
        {
            tip = chain.Next(tip, slot, full: false, (byte)slot);
            held ??= tip;
            Assert.That(importer.Import(tip.Forked, tip.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        }

        ExecutionPayloadEnvelopeImportResult heldBeforePersist = importer.ImportEnvelope(held!.Envelope);
        store.PutState(held.Root, BeaconStateGloas.Encode(held.PostState));
        store.PutState(unheld.Root, BeaconStateGloas.Encode(unheld.PostState));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.ImportEnvelope(anchor.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(heldBeforePersist, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock), "fixture: the held block's state aged out of every tier");
            Assert.That(importer.ImportEnvelope(held.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(importer.ImportEnvelope(unheld.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
        }
    }

    /// <summary>
    /// specs/gloas/fork-choice.md <c>on_attester_slashing</c> validates against <c>store.block_states[store.justified_checkpoint.root]</c>
    /// for as long as that checkpoint is justified. A Gloas checkpoint justified without finality must stay resolvable
    /// after more epoch boundaries than the boundary tier holds, or every attester slashing is refused.
    /// </summary>
    [Test]
    public void Justified_gloas_checkpoint_state_outlives_more_epoch_boundaries_than_the_boundary_tier_holds()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        const ulong justifiedEpoch = 2;
        const ulong lastSlot = 12 * ForkSlot;
        TestLogger logger = new();
        SlotClock clock = new(chain.Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + (lastSlot + 1) * chain.Spec.SecondsPerSlot)));
        IBlockImporter importer = CreateFactory(chain, anchor.PostState.Validators!, new SignedGloasChain.EnvelopeEngine(), chain.CreateStore(), clock, new OneLoggerLogManager(new ILogger(logger)))
            .Create(new ForkedBeaconState.OfGloas(anchor.PostState), anchor.Forked, anchor.Root);

        SignedGloasChain.Block justified = chain.Next(anchor, justifiedEpoch * ForkSlot, full: false, 0xA2);
        Assert.That(importer.Import(justified.Forked, justified.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));

        // Epoch 3 opens with three blocks of eight one-committee votes (1536 of 2048 validators) for the epoch 2 target; nothing
        // after them votes, so epoch 2 is justified at the next epoch transition and never finalized. Two epochs of blocks then
        // push its state out of the per-block tier, and later epoch starts push it out of the boundary tier.
        ulong[] slots = [.. Enumerable.Range(3 * (int)ForkSlot, 2 * (int)ForkSlot).Select(static s => (ulong)s), .. Enumerable.Range(5, 8).Select(static e => (ulong)e * ForkSlot)];
        SignedGloasChain.Block tip = justified;
        foreach (ulong slot in slots)
        {
            ulong voteGroup = slot - 3 * ForkSlot;
            tip = chain.Next(tip, slot, full: false, (byte)(slot % 100), attestations: voteGroup < 3 ? (state, cache) => TargetVotes(state, cache, justifiedEpoch, justified.Root, (int)voteGroup) : null);
            Assert.That(importer.Import(tip.Forked, tip.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        }

        HeadView head = importer.ComputeHead();
        importer.OnGossipAttesterSlashing(DoubleVote(justified.PostState, justifiedEpoch + 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tip.Signed.Message!.Slot, Is.EqualTo(lastSlot), "fixture");
            Assert.That(head.Justified, Is.EqualTo(new CheckpointRef(justifiedEpoch, justified.Root)), "fixture: the votes justify epoch 2");
            Assert.That(head.Finalized, Is.EqualTo(new CheckpointRef(1, anchor.Root)), "fixture: nothing past the anchor is finalized");
            Assert.That(logger.LogList.Where(static l => l.Contains("attester slashing")), Is.Empty, "the slashing verifies against the justified state");
            Assert.That(importer.ImportEnvelope(justified.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "the justified state is still held");
        }

        importer.OnFinalized(new CheckpointRef(justifiedEpoch, justified.Root));
        Assert.That(importer.ImportEnvelope(anchor.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock), "finalizing a later checkpoint releases the anchor state");
    }

    /// <summary>A node anchored on a Gloas checkpoint follows no Fulu lineage, so a Fulu block's proposer is left to the transition, which refuses it.</summary>
    [Test]
    public void Gloas_anchor_leaves_a_fulu_block_proposer_check_to_the_transition()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        IBlockImporter importer = CreateFactoryImporter(chain, anchor, new SignedGloasChain.EnvelopeEngine(), chain.CreateStore());

        Assert.That(importer.IsExpectedProposer(new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock)), Is.True);
    }

    /// <summary>
    /// The pinned finalized state is not an entry of the epoch-boundary tier: it stays resolvable after more checkpoint
    /// retentions than that tier holds, while a checkpoint state retained as early ages out.
    /// </summary>
    [Test]
    public void Pinned_gloas_state_outlives_more_checkpoint_retentions_than_the_boundary_tier_holds()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block block = chain.Next(null, ForkSlot, full: false, 0xA1);
        PostStateCache states = new(chain.CreateStore(), chain.Spec, null, null);
        states.PinGloas(TestItem.KeccakA, block.PostState);
        states.RetainGloas(TestItem.KeccakB, block.PostState, checkpointCandidate: true);

        // Past the per-block tier too, so only a boundary retention could still hold the early root.
        for (int i = 0; i < 2 * (int)ForkSlot + 9; i++)
        {
            states.RetainGloas(Keccak.Compute(BitConverter.GetBytes(i)), block.PostState, checkpointCandidate: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(states.GetGloasBlockState(TestItem.KeccakA), Is.SameAs(block.PostState));
            Assert.That(states.GetGloasBlockState(TestItem.KeccakB), Is.Null, "fixture: the boundary tier evicted the early checkpoint");
        }
    }

    /// <summary>
    /// A persisted Gloas state is not a Fulu state: the Fulu getter answers unknown rather than throwing from its decoder.
    /// The Gloas getter reads it back, but only for a root fork choice holds at a Gloas slot, so a gossip block naming a
    /// persisted root costs no store read.
    /// </summary>
    [Test]
    public void Persisted_gloas_state_is_unknown_to_the_fulu_getter_and_read_back_by_the_gloas_getter_only_for_a_held_root([Values] bool held)
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block block = chain.Next(null, ForkSlot, full: false, 0xA1);
        BeaconChainStore store = chain.CreateStore();
        store.PutState(block.Root, BeaconStateGloas.Encode(block.PostState));
        PostStateCache states = new(store, chain.Spec, null, null, _ => held);

        BeaconStateFulu? fulu = null;
        Assert.DoesNotThrow(() => fulu = states.GetBlockState(block.Root));
        BeaconStateGloas? gloas = states.GetGloasBlockState(block.Root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fulu, Is.Null);
            Assert.That(gloas is null ? null : SszRoots.HashTreeRoot(gloas), Is.EqualTo(held ? block.Signed.Message!.StateRoot : null));
            Assert.That(states.GetGloasBlockState(block.Root), Is.SameAs(gloas), "a state read back once is retained, not decoded again");
        }
    }

    /// <summary>
    /// Finalizing a Gloas checkpoint persists its Gloas state and moves the anchor to it, or a restart would resume from
    /// the last Fulu checkpoint. The restarted importer is built on that Gloas anchor and replays the stored blocks above
    /// it, the first of which builds full on the anchor and so stands in for the anchor's envelope.
    /// </summary>
    [Test]
    public async Task Gloas_finalization_persists_a_gloas_anchor_that_a_restart_resumes_from_and_replays_above()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block checkpoint = chain.Next(anchor, 2 * ForkSlot, full: false, 0xA2);
        SignedGloasChain.Block fullChild = chain.Next(checkpoint, 2 * ForkSlot + 1, full: true, 0xA3);
        SignedGloasChain.Block tip = chain.Next(fullChild, 2 * ForkSlot + 2, full: false, 0xA4);
        BeaconChainStore store = chain.CreateStore();
        // Near the chain: a leaf whose justification is two epochs behind the clock is not viable for the head.
        SlotClock clock = new(chain.Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + (2 * ForkSlot + 4) * chain.Spec.SecondsPerSlot)));
        IBlockImporter importer = CreateFactoryImporter(chain, anchor, new SignedGloasChain.EnvelopeEngine(), store, clock);
        Assert.That(importer.Import(checkpoint.Forked, checkpoint.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.ImportEnvelope(checkpoint.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
        Assert.That(importer.Import(fullChild.Forked, fullChild.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.Import(tip.Forked, tip.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
        importer.ComputeHead();

        importer.OnFinalized(new CheckpointRef(2, checkpoint.Root));

        Assert.That(importer.ImportEnvelope(anchor.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "no tier retains the anchor state; it is still fork choice's justified checkpoint, so the justified pin serves it");
        Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot), Is.True);
        Assert.That(store.TryGetState(anchorRoot!, out byte[]? stateSsz), Is.True);
        Assert.That(store.TryGetForkedBlock(anchorRoot!, out ForkedSignedBeaconBlock? anchorBlock), Is.True);
        ForkedBeaconState anchorState = BeaconStateCodec.DecodeForked(stateSsz, chain.Spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchorRoot, Is.EqualTo(checkpoint.Root));
            Assert.That(anchorSlot, Is.EqualTo(2 * ForkSlot));
            Assert.That(anchorState is ForkedBeaconState.OfGloas { State: { } gloas } ? SszRoots.HashTreeRoot(gloas) : null, Is.EqualTo(checkpoint.Signed.Message!.StateRoot));
            Assert.That(new PubkeyCache().TryLoad(store, checkpoint.PostState.Validators!), Is.True, "the pubkey cache is persisted with the anchor");
        }

        SignedGloasChain.EnvelopeEngine restartedEngine = new();
        IBlockImporter restarted = CreateFactory(chain, ((ForkedBeaconState.OfGloas)anchorState).State.Validators!, restartedEngine, store, clock).Create(anchorState, anchorBlock!, anchorRoot!);
        BeaconSyncOrchestrator orchestrator = CreateOrchestrator(chain, store, restartedEngine, clock);
        orchestrator.Initialize(restarted, anchorBlock!, anchorRoot!);
        await orchestrator.ReplayStoredBlocksAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(orchestrator.SyncTip, Is.EqualTo((tip.Root, tip.Signed.Message!.Slot)));
            Assert.That(restarted.IsKnown(fullChild.Root) && restarted.IsKnown(tip.Root), Is.True);
            Assert.That(restarted.ImportEnvelope(checkpoint.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.AlreadyKnown), "the replayed full child recorded the anchor's payload");
            Assert.That(restartedEngine.FcuCalls, Is.EqualTo(new[] { (tip.Bid.ParentBlockHash!, checkpoint.Bid.ParentBlockHash!, checkpoint.Bid.ParentBlockHash!) }));
        }
    }

    /// <summary>
    /// The engine kick and every head step without a finalized hash point the execution layer at the anchor's execution
    /// hash. For a Gloas anchor that is the bid's <c>parent_block_hash</c> (specs/gloas/fork-choice.md
    /// <c>notify_forkchoice_updated</c>), equal to the anchor state's <c>latest_block_hash</c>; the bid's <c>block_hash</c>
    /// names a payload the execution layer may never have received.
    /// </summary>
    [Test]
    public async Task Gloas_anchor_execution_hash_is_its_bid_parent_block_hash()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.EnvelopeEngine engine = new();
        IBlockImporter importer = Substitute.For<IBlockImporter>();
        importer.ComputeHead().Returns(new HeadView(anchor.Root, ForkSlot, Keccak.Zero, null, null, new CheckpointRef(1, anchor.Root), new CheckpointRef(1, anchor.Root)));
        BeaconSyncOrchestrator orchestrator = CreateOrchestrator(chain, chain.CreateStore(), engine, new SlotClock(chain.Spec, Timestamper.Default));

        orchestrator.Initialize(importer, anchor.Forked, anchor.Root);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.Bid.ParentBlockHash, Is.EqualTo(anchor.PostState.LatestBlockHash), "process_execution_payload_bid asserts it");
            Assert.That(engine.FcuCalls, Has.Count.EqualTo(1));
            Assert.That(engine.FcuCalls[0].Finalized, Is.EqualTo(anchor.Bid.ParentBlockHash));
        }
    }

    /// <summary>
    /// A gossip block names any parent root it likes. A persisted state of a root fork choice does not hold must cost
    /// no store read, or every such block costs a manifest read and a full state decode.
    /// </summary>
    [Test]
    public void Gloas_block_on_a_persisted_parent_fork_choice_does_not_hold_costs_no_store_read()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block anchor = chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block unheld = chain.Next(anchor, ForkSlot + 1, full: false, 0xB0);
        SignedGloasChain.Block child = chain.Next(unheld, ForkSlot + 2, full: false, 0xB1);
        MemColumnsDb<BeaconChainDbColumns> columns = new();
        BeaconChainStore store = new(columns, chain.Spec);
        store.PutState(unheld.Root, BeaconStateGloas.Encode(unheld.PostState));
        IBlockImporter importer = CreateFactoryImporter(chain, anchor, new SignedGloasChain.EnvelopeEngine(), store);
        MemDb states = (MemDb)columns.GetColumnDb(BeaconChainDbColumns.States);
        long readsBefore = states.ReadsCount;

        bool expectedProposer = importer.IsExpectedProposer(child.Forked);
        BlockImportResult result = importer.Import(child.Forked, child.Root, verifySignatures: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expectedProposer, Is.True, "an unretained parent defers to the transition");
            Assert.That(result, Is.EqualTo(BlockImportResult.UnknownParent));
            Assert.That(states.ReadsCount - readsBefore, Is.Zero);
        }
    }

    /// <summary>
    /// A root that becomes justified while it is the oldest entry of a full boundary tier, and already out of the
    /// per-block tier, is pinned before the next checkpoint candidate evicts it.
    /// </summary>
    [Test]
    public void Newly_justified_oldest_boundary_entry_is_pinned_before_the_next_candidate_evicts_it()
    {
        SignedGloasChain chain = new();
        BeaconStateGloas state = chain.Next(null, ForkSlot, full: false, 0xA1).PostState;
        Hash256 justified = TestItem.KeccakA;
        Hash256 current = Keccak.Zero;
        PostStateCache states = new(chain.CreateStore(), chain.Spec, null, null, justifiedRoot: () => current);

        states.RetainGloas(justified, state, checkpointCandidate: true);
        RetainDistinct(states, state, 7, checkpointCandidate: true, seed: 0);
        RetainDistinct(states, state, 2 * (int)ForkSlot, checkpointCandidate: false, seed: 1000);
        current = justified;
        RetainDistinct(states, state, 1, checkpointCandidate: true, seed: 2000);

        Assert.That(states.GetGloasBlockState(justified), Is.SameAs(state));
    }

    /// <summary>
    /// specs/phase0/beacon-chain.md <c>weigh_justification_and_finalization</c> may finalize the justified checkpoint in the
    /// same import that justifies the next one, before <c>OnFinalized</c> reads the finalized state to persist it. The
    /// outgoing justified state therefore stays pinned for one more justification or until finalization moves, and no longer.
    /// </summary>
    [Test]
    public void Outgoing_justified_state_stays_pinned_for_one_more_justification()
    {
        SignedGloasChain chain = new();
        BeaconStateGloas state = chain.Next(null, ForkSlot, full: false, 0xA1).PostState;
        Hash256[] justified = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC];
        Hash256 current = Keccak.Zero;
        PostStateCache states = new(chain.CreateStore(), chain.Spec, null, null, justifiedRoot: () => current);
        BeaconStateGloas?[] firstAfter = new BeaconStateGloas?[justified.Length];

        // Each root is justified, pinned, and then aged out of both tiers before the next one is justified.
        for (int i = 0; i < justified.Length; i++)
        {
            states.RetainGloas(justified[i], state, checkpointCandidate: true);
            current = justified[i];
            RetainDistinct(states, state, 2 * (int)ForkSlot + 8, checkpointCandidate: true, seed: 1000 * i);
            firstAfter[i] = states.GetGloasBlockState(justified[0]);
        }

        BeaconStateGloas? outgoingBeforeFinality = states.GetGloasBlockState(justified[1]);
        states.PinGloas(TestItem.KeccakD, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstAfter[0], Is.SameAs(state), "fixture: the justified pin holds the first root");
            Assert.That(firstAfter[1], Is.SameAs(state), "the next justification keeps the outgoing state");
            Assert.That(firstAfter[2], Is.Null, "two justifications later the first state is released");
            Assert.That(outgoingBeforeFinality, Is.SameAs(state));
            Assert.That(states.GetGloasBlockState(justified[1]), Is.Null, "finalization releases the outgoing state");
            Assert.That(states.GetGloasBlockState(justified[2]), Is.SameAs(state), "finalization keeps the current justified state");
        }
    }

    /// <summary>
    /// A newly justified root whose state is not held pins nothing: the states it would displace stay pinned, and its own state
    /// is still found once it is retained rather than being shadowed by an empty pin.
    /// </summary>
    [Test]
    public void Justifying_a_root_whose_state_is_not_held_keeps_the_existing_pins()
    {
        SignedGloasChain chain = new();
        BeaconStateGloas state = chain.Next(null, ForkSlot, full: false, 0xA1).PostState;
        Hash256 current = Keccak.Zero;
        PostStateCache states = new(chain.CreateStore(), chain.Spec, null, null, justifiedRoot: () => current);
        Hash256[] pinned = [TestItem.KeccakA, TestItem.KeccakB];
        for (int i = 0; i < pinned.Length; i++)
        {
            states.RetainGloas(pinned[i], state, checkpointCandidate: true);
            current = pinned[i];
            RetainDistinct(states, state, 2 * (int)ForkSlot + 8, checkpointCandidate: true, seed: 1000 * i);
        }

        current = TestItem.KeccakC;
        RetainDistinct(states, state, 1, checkpointCandidate: true, seed: 5000);
        BeaconStateGloas? outgoing = states.GetGloasBlockState(pinned[0]);
        BeaconStateGloas? pinnedJustified = states.GetGloasBlockState(pinned[1]);
        states.RetainGloas(TestItem.KeccakC, state, checkpointCandidate: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outgoing, Is.SameAs(state), "the outgoing justified state is not released for a root with no state");
            Assert.That(pinnedJustified, Is.SameAs(state));
            Assert.That(states.GetGloasBlockState(TestItem.KeccakC), Is.SameAs(state), "the root's state is found once retained");
        }
    }

    /// <summary>A persisted state read back from the store is retained in the per-block tier, so it never evicts a checkpoint candidate from the boundary tier.</summary>
    [Test]
    public void Persisted_gloas_state_read_back_does_not_evict_a_checkpoint_candidate()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block block = chain.Next(null, ForkSlot, full: false, 0xA1);
        BeaconChainStore store = chain.CreateStore();
        store.PutState(block.Root, BeaconStateGloas.Encode(block.PostState));
        PostStateCache states = new(store, chain.Spec, null, null, _ => true);
        Hash256 candidate = TestItem.KeccakA;
        states.RetainGloas(candidate, block.PostState, checkpointCandidate: true);
        RetainDistinct(states, block.PostState, 7, checkpointCandidate: true, seed: 0);
        RetainDistinct(states, block.PostState, 2 * (int)ForkSlot, checkpointCandidate: false, seed: 1000);

        Assert.That(states.GetGloasBlockState(block.Root), Is.Not.Null, "fixture: the persisted state is read back");
        Assert.That(states.GetGloasBlockState(candidate), Is.SameAs(block.PostState));
    }

    /// <summary>
    /// A corrupt persisted snapshot that still passes the store's length checks must not throw out of every import,
    /// envelope and proposer check that names its root; it is logged and treated as absent.
    /// </summary>
    [TestCase(10, TestName = "Corrupt_persisted_gloas_state_is_absent(too short for a slot)")]
    [TestCase(200, TestName = "Corrupt_persisted_gloas_state_is_absent(malformed body)")]
    public void Corrupt_persisted_gloas_state_is_absent(int length)
    {
        SignedGloasChain chain = new();
        byte[] ssz = new byte[length];
        new Random(length).NextBytes(ssz);
        if (length >= 48)
        {
            BitConverter.TryWriteBytes(ssz.AsSpan(40), ForkSlot + 1);
        }

        BeaconChainStore store = chain.CreateStore();
        store.PutState(TestItem.KeccakA, ssz);
        TestLogger logger = new();
        PostStateCache states = new(store, chain.Spec, null, null, _ => true, logManager: new OneLoggerLogManager(new ILogger(logger)));

        BeaconStateGloas? state = null;
        Assert.DoesNotThrow(() => state = states.GetGloasBlockState(TestItem.KeccakA));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state, Is.Null);
            Assert.That(logger.LogList.Where(static l => l.Contains(TestItem.KeccakA.ToString())), Is.Not.Empty);
        }
    }

    private static void RetainDistinct(PostStateCache states, BeaconStateGloas state, int count, bool checkpointCandidate, int seed)
    {
        for (int i = 0; i < count; i++)
        {
            states.RetainGloas(Keccak.Compute(BitConverter.GetBytes(seed + i)), state, checkpointCandidate);
        }
    }

    [Test]
    public void Factory_refuses_an_anchor_state_and_block_of_different_forks([Values] bool gloasState)
    {
        SignedGloasChain chain = new();
        SignedGloasChain.Block gloas = chain.Next(null, ForkSlot, full: false, 0xA1);
        ForkedBeaconState state = gloasState ? new ForkedBeaconState.OfGloas(gloas.PostState) : new ForkedBeaconState.OfFulu(chain.AnchorState);
        ForkedSignedBeaconBlock block = gloasState ? new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock) : gloas.Forked;

        Assert.Throws<ArgumentException>(() => CreateFactory(chain, gloas.PostState.Validators!, new SignedGloasChain.EnvelopeEngine(), chain.CreateStore(), new SlotClock(chain.Spec, Timestamper.Default)).Create(state, block, gloas.Root));
    }

    private static IBlockImporter CreateFactoryImporter(SignedGloasChain chain, SignedGloasChain.Block anchor, IEngineDriver engine, BeaconChainStore store, SlotClock? clock = null)
    {
        ForkedBeaconState state = new ForkedBeaconState.OfGloas(anchor.PostState);
        return CreateFactory(chain, anchor.PostState.Validators!, engine, store, clock ?? new SlotClock(chain.Spec, Timestamper.Default)).Create(state, anchor.Forked, anchor.Root);
    }

    private static BlockImporterFactory CreateFactory(SignedGloasChain chain, Validator[] validators, IEngineDriver engine, BeaconChainStore store, SlotClock clock, ILogManager? logManager = null)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(validators);
        return new BlockImporterFactory(chain.Spec, store, pubkeys, engine, new BeaconChainConfig(), logManager ?? LimboLogs.Instance, new DataColumnSidecarPool(), clock);
    }

    /// <summary>Full one-committee votes for the <paramref name="targetEpoch"/> target <paramref name="targetRoot"/> from the eight slots of <paramref name="group"/>.</summary>
    private static AttestationGloas[] TargetVotes(BeaconStateGloas state, EpochCache cache, ulong targetEpoch, Hash256 targetRoot, int group)
    {
        CommitteeCache committees = cache.GetCommitteeCache(state, targetEpoch);
        ulong firstSlot = targetEpoch * ForkSlot + (ulong)(8 * group);
        return [.. Enumerable.Range(0, 8).Select(i => CommitteeAttestation(state, VoteFor(state, firstSlot + (ulong)i, targetEpoch, targetRoot), committees, 0, sign: false))];
    }

    /// <summary>Two signed, conflicting votes by validators 0 and 1 for the same target epoch.</summary>
    private static AttesterSlashingGloas DoubleVote(BeaconStateGloas state, ulong targetEpoch) => new()
    {
        Attestation1 = SignedIndexedAttestation(state, Vote(targetEpoch * ForkSlot, targetEpoch - 1, targetEpoch, 0x51), [0, 1]),
        Attestation2 = SignedIndexedAttestation(state, Vote(targetEpoch * ForkSlot, targetEpoch - 1, targetEpoch, 0x61), [0, 1]),
    };

    private static BeaconSyncOrchestrator CreateOrchestrator(SignedGloasChain chain, BeaconChainStore store, IEngineDriver engine, SlotClock clock)
    {
        IBeaconSyncPeerPool pool = Substitute.For<IBeaconSyncPeerPool>();
        return new BeaconSyncOrchestrator(
            new BeaconChainConfig(),
            chain.Spec,
            store,
            Substitute.For<IBlockImporterFactory>(),
            engine,
            pool,
            new RangeSync(pool, LimboLogs.Instance, new DataColumnSidecarPool(), chain.Spec, RangeSyncTests.ClockAtGenesis(chain.Spec)),
            clock,
            new GossipRouter(chain.Spec, clock, LimboLogs.Instance),
            new BeaconChainStatusHolder(chain.Spec, Timestamper.Default),
            LimboLogs.Instance)
        {
            GossipStarted = true,
        };
    }
}
