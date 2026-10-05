// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using NSubstitute;
using NUnit.Framework.Constraints;
using Snappier;

namespace Nethermind.BeaconChain.Test.Sync;

[HardTimeout(60_000)]
public class BlockImporterTests
{
    private static readonly Hash256 NodeId = new([.. Enumerable.Repeat((byte)0x42, 32)]);
    private static readonly Hash256 UnknownBlockRoot = new([.. Enumerable.Repeat((byte)0x99, 32)]);

    private static NodeColumnCustody BaseCustody() => new(NodeId, Eip7594DasConstants.CustodyRequirement);

    [Test]
    public void Import_preserves_state_time_and_data_availability([Values] bool verifySignatures, [Values] bool available)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        IDataAvailabilityRule availability = Substitute.For<IDataAvailabilityRule>();
        availability.IsDataAvailable(Arg.Any<BeaconBlock>(), Arg.Any<Hash256>(), chain.Spec).Returns(available);
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), availability: availability);
        Assert.That(chain.AnchorState.GenesisTime, Is.Not.EqualTo(chain.Spec.GenesisTime),
            "the transition's timestamp comes from the checkpoint state");

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(available || !verifySignatures ? BlockImportResult.Imported : BlockImportResult.DataUnavailable),
            "gossip checks must preserve import results, including trusted replay and availability deferral");
        Assert.That(((IBlockImporter)importer).RejectGossip, Is.False);
    }

    [Test]
    public void Unavailable_data_precedes_fulu_proposal_validation([Values] bool wrongProposer)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        IDataAvailabilityRule availability = Substitute.For<IDataAvailabilityRule>();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), availability: availability);
        if (wrongProposer) chain.Block.Message!.ProposerIndex = ulong.MaxValue;
        else chain.Block.Message!.Body!.ExecutionPayload!.Timestamp++;

        BlockImportResult result = importer.Import(chain.Block, SszRoots.HashTreeRoot(chain.Block.Message!), verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable),
            "the import availability gate must run before proposal validation, independently of the router's verdict");
    }

    [Test]
    public void Blob_block_with_every_sampled_column_held_and_verified_imports()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "eight verified columns out of 128 is exactly what a base-custody node can hold, and must suffice");
        Assert.That(importer.IsKnown(chain.BlockRoot), Is.True);
        Assert.That(custody.SampledColumns, Has.Count.EqualTo(8), "the fixture node is a base-custody node, not a supernode");
    }

    [TestCase(true, TestName = "Blob_block_missing_one_custody_column_is_deferred")]
    [TestCase(false, TestName = "Blob_block_missing_a_sampled_but_not_custodied_column_is_deferred")]
    public void Blob_block_missing_a_required_column_is_deferred(bool custodied)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custodied ? custody.CustodyColumns[0] : custody.SampledColumns.First(c => !custody.CustodyColumns.Contains(c));
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        BlockImporter importer = CreateImporter(chain, custody, pool, warnings);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), custodied
            ? "missing columns are retryable, not a permanent rejection"
            : "custody columns alone are not enough: the per-slot sample must succeed too");
        Assert.That(importer.IsKnown(chain.BlockRoot), Is.False, "a block whose data is unavailable must not enter fork choice");
        Assert.That(warnings.Messages, Has.None.Contains("blob data is not yet available"), "a block trailing its columns is routine at the head, not a warning");
    }

    [Test]
    public void Blob_block_is_deferred_while_the_node_identity_is_unknown()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => (ulong)c));
        BlockImporter importer = CreateImporter(chain, custody: null, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), "a rule that cannot say which columns it needs cannot say a block is available, even holding all 128");
    }

    [TestCase(true, TestName = "Held_column_that_fails_kzg_verification_does_not_count")]
    [TestCase(false, TestName = "Held_column_addressed_to_a_different_block_does_not_count")]
    public void Invalid_held_column_does_not_count(bool invalidProof)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecar tampered = chain.Columns[(int)custody.CustodyColumns[0]];
        if (invalidProof)
        {
            byte[] cell = tampered.Column![0].AsSpan().ToArray();
            cell[0] ^= 0xFF;
            tampered.Column[0] = SszBlobCell.FromSpan(cell);
        }
        else
        {
            // Same index, same commitments, valid proofs: only the header names another block.
            tampered.SignedBlockHeader!.Message!.Slot = 999;
        }
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), invalidProof
            ? "holding a column is not availability; the column must verify against the block's commitments"
            : null);
    }

    [Test]
    public void Block_without_blob_commitments_imports_with_no_columns_and_no_identity()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool());

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "a block with no blobs needs no columns");
    }

    [TestCase(1UL, GossipRouter.MaximumGossipClockDisparityMs + 1, BlockImportResult.Invalid)]
    [TestCase(1UL, GossipRouter.MaximumGossipClockDisparityMs, BlockImportResult.FutureSlot)]
    [TestCase(1UL, -1_200_000L, BlockImportResult.Imported)]
    [TestCase(1UL << 40, -1_200_000L, BlockImportResult.Invalid)]
    public void Block_after_the_clock_slot_is_refused_before_its_state_transition(ulong slot, long millisecondsBeforeSlotOne, BlockImportResult expected)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        SlotClock clock = new(chain.Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeMilliseconds((long)(chain.Spec.GenesisTime + chain.Spec.SecondsPerSlot) * 1000 - millisecondsBeforeSlotOne).UtcDateTime));
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), warnings, importClock: clock, failedBlocks: failed);
        BeaconBlock block = chain.Block.Message!;
        block.Slot = slot;
        Hash256 root = SszRoots.HashTreeRoot(block);

        BlockImportResult result = GloasBlockImporterTests.ImportOrFailIfStuck(importer, new ForkedSignedBeaconBlock.OfFulu(chain.Block), root);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(expected));
        Assert.That(failed.Contains(root), Is.False, "a block from a slot the clock has not reached may still become valid");
        Assert.That(warnings.Messages.Any(w => w.Contains("before its state transition")), Is.EqualTo(expected == BlockImportResult.Invalid));
    }

    [Test]
    public void Block_before_its_slot_starts_imports_once_the_slot_starts([Values] bool slotTickFirst, [Values] bool trusted)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        DateTime slotStart = TickFinalityFixture.SlotStart(chain.Spec, 1);
        ManualTimestamper time = new(slotStart.AddMilliseconds(-GossipRouter.MaximumGossipClockDisparityMs));
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), importClock: new SlotClock(chain.Spec, time), failedBlocks: failed);
        BlsSignature genuine = chain.Block.Signature;
        chain.Block.Signature = new BlsSignature(SignatureSets.G2PointAtInfinity);
        BlockImportResult forged = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);
        chain.Block.Signature = genuine;

        BlockImportResult early = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: !trusted);
        bool knownEarly = importer.IsKnown(chain.BlockRoot);
        if (slotTickFirst)
        {
            importer.OnSlotTick(1);
        }
        else
        {
            time.Set(slotStart);
        }

        BlockImportResult onTime = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: !trusted);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(forged, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(early, Is.EqualTo(BlockImportResult.FutureSlot));
        Assert.That(knownEarly, Is.False, "fork choice has not seen the block before its slot");
        Assert.That(failed.Contains(chain.BlockRoot), Is.False);
        Assert.That(onTime, Is.EqualTo(BlockImportResult.Imported));
    }

    [TestCase(0UL, 11500L, 1UL, true)]
    [TestCase(0UL, 11500L, 5UL, true)]
    [TestCase(1UL, 1000L, 0UL, true)]
    [TestCase(1UL, 3500L, 0UL, true)]
    [TestCase(1UL, 3999L, 0UL, true)]
    [TestCase(1UL, 4000L, 0UL, false)]
    [TestCase(1UL, 5000L, 0UL, false)]
    [TestCase(2UL, 1000L, 0UL, false)]
    [TestCase(1UL, 1000L, 4UL, true)]
    public void Proposer_boost_follows_the_clock_at_import(ulong clockSlot, long msIntoSlot, ulong newPayloadSeconds, bool boosted)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        ForkChoiceSnapshotHolder snapshots = new();
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + clockSlot * chain.Spec.SecondsPerSlot).AddMilliseconds(msIntoSlot));
        SlotClock clock = new(chain.Spec, timestamper);
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), engine: new ValidPayloadEngine(() => timestamper.Add(TimeSpan.FromSeconds(newPayloadSeconds))), forkChoiceSnapshots: snapshots, importClock: clock);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);
        importer.ComputeHead();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(snapshots.Current!.ProposerBoostRoot, Is.EqualTo(boosted ? chain.BlockRoot : Hash256.Zero));
    }

    [Test]
    public void Trusted_store_replay_imports_a_blob_block_without_its_columns()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BlockImporter importer = CreateImporter(chain, BaseCustody(), new DataColumnSidecarPool());

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: false);

        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the store only holds blocks that already passed the gate, and their columns are not persisted; re-checking would stall every restart");
    }

    [Test]
    public async Task Factory_built_importer_admits_a_blob_block_once_the_columns_of_the_discovery_identity_are_held()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        NodeColumnCustody custody = new DiscoveryNodeCustodySource(discovery).Current!;
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporterFactory factory = new(chain.Spec, store, chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, pool, chain.ClockAtSlot(chain.Block.Message!.Slot), discovery);
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(custody.CustodyGroupCount, Is.EqualTo(discovery.LocalCustody.CustodyGroupCount), "the importer demands the custody discovery advertises");
        Assert.That(custody.SampledColumns, Has.Count.EqualTo(8));
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the production wiring, end to end: eight held columns of this node's real identity admit the block");
    }

    [Test]
    public void Factory_built_importer_applies_the_custody_rule_and_fails_closed_without_discovery()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => (ulong)c));
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, pool, chain.ClockAtSlot(chain.Block.Message!.Slot));
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), "no discovery means no node id, so the custody rule has no columns to demand and must defer rather than pass");
    }

    [Test]
    public void Blob_block_below_the_availability_window_imports_with_no_columns_and_no_identity()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), clock: chain.ClockAtEpoch(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1));

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "outside the retention window nobody serves columns, so the gate must not wait for them");
        Assert.That(importer.IsKnown(chain.BlockRoot), Is.True);
    }

    [Test]
    public void ComputeHead_publishes_a_fork_choice_snapshot_carrying_the_head_it_chose()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = CreateImporter(chain, custody, pool, forkChoiceSnapshots: snapshots);
        ForkChoiceSnapshot? beforeAnyHead = snapshots.Current;

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);
        HeadView head = importer.ComputeHead();
        ForkChoiceSnapshot? published = snapshots.Current;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(beforeAnyHead, Is.Null, "nothing is published until a head has been computed");
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(published, Is.Not.Null);
        Assert.That(published!.Nodes.Select(n => n.Root), Is.EqualTo(new[] { chain.AnchorRoot, chain.BlockRoot }));
        Assert.That(published.Nodes[1].ExecutionStatus, Is.EqualTo(ExecutionStatus.Valid), "the engine's verdict reached fork choice before the copy was taken");
        Assert.That(published.JustifiedCheckpoint, Is.EqualTo(head.Justified));
        Assert.That(published.FinalizedCheckpoint, Is.EqualTo(head.Finalized));
        Assert.That(published.Nodes.Select(n => n.Root), Does.Contain(head.HeadRoot));
    }

    [Test]
    public void Factory_built_importer_publishes_into_the_holder_it_is_given()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, new DataColumnSidecarPool(), new SlotClock(chain.Spec, Timestamper.Default), forkChoiceSnapshots: snapshots);
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);

        importer.ComputeHead();

        Assert.That(snapshots.Current?.Nodes.Select(n => n.Root), Is.EqualTo(new[] { chain.AnchorRoot }), "the container's holder must be the one the importer writes to");
    }

    [Test]
    public void ComputeHead_after_an_import_publishes_the_head_states_proposer_lookahead()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        ProposerLookaheadHolder lookaheads = new();
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, new DataColumnSidecarPool(), clock: chain.ClockAtSlot(chain.Block.Message!.Slot), proposerLookaheads: lookaheads);
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);
        ProposerLookaheadSnapshot? beforeAnyHead = lookaheads.Current;
        HeadView head = importer.ComputeHead();
        ProposerLookaheadSnapshot? published = lookaheads.Current;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(beforeAnyHead, Is.Null, "nothing is published until a head has been computed");
        Assert.That(head.HeadRoot, Is.EqualTo(chain.BlockRoot));
        Assert.That(published?.Epoch, Is.EqualTo(0UL));
        // Nothing precedes the head's epoch, so the shuffling was decided below the fork-choice tree root.
        Assert.That(published?.DependentRoot, Is.EqualTo(chain.AnchorRoot));
        Assert.That(published!.TryGetProposer(chain.Block.Message.Slot, out ulong proposer) ? proposer : (ulong?)null, Is.EqualTo(chain.Block.Message.ProposerIndex));
        Assert.That(published.TryGetProposer(Presets.ProposerLookaheadSlots, out _), Is.False, "the lookahead covers two epochs");
    }

    [Test]
    public void ComputeHead_after_a_reorg_publishes_the_new_heads_proposer_lookahead()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ProposerLookaheadHolder lookaheads = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), proposerLookaheads: lookaheads);
        UnsignedChain.Equivocation scenario = chain.BuildEquivocation();
        // B's branch reaches epoch 1, so its lookahead differs from A's branch in both epoch and dependent root.
        UnsignedChain.ChainBlock nextEpoch = chain.Extend(scenario.B.Root, slot: chain.Spec.SlotsPerEpoch + 1, payloadHashByte: 0xb2);
        importer.OnSlotTick(nextEpoch.Block.Message!.Slot + 1);
        BlockImportResult[] imported =
        [
            importer.Import(scenario.A.Block, scenario.A.Root, verifySignatures: false),
            importer.Import(scenario.B.Block, scenario.B.Root, verifySignatures: false),
            importer.Import(scenario.Voted.Block, scenario.Voted.Root, verifySignatures: false),
            importer.Import(nextEpoch.Block, nextEpoch.Root, verifySignatures: false),
        ];
        Hash256 headBefore = importer.ComputeHead().HeadRoot;
        ProposerLookaheadSnapshot? before = lookaheads.Current;

        BlockImportResult slashingImported = importer.Import(scenario.Slashing.Block, scenario.Slashing.Root, verifySignatures: false);
        Hash256 headAfter = importer.ComputeHead().HeadRoot;
        ProposerLookaheadSnapshot? after = lookaheads.Current;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(imported.Append(slashingImported), Is.All.EqualTo(BlockImportResult.Imported));
        Assert.That((headBefore, headAfter), Is.EqualTo((scenario.Voted.Root, nextEpoch.Root)), "the slashing moves the head to B's branch");
        Assert.That((before?.Epoch, before?.DependentRoot), Is.EqualTo(((ulong?)0, (Hash256?)chain.AnchorRoot)));
        Assert.That((after?.Epoch, after?.DependentRoot), Is.EqualTo(((ulong?)1, (Hash256?)scenario.B.Root)), "the new head's lookahead replaces the old branch's");
    }

    [Test]
    public void Column_gossip_checks_a_child_of_the_imported_head_against_the_published_lookahead([Values] bool expectedProposer)
    {
        const ulong column = 5;
        const ulong childSlot = 2;
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        ForkChoiceSnapshotHolder snapshots = new();
        ProposerLookaheadHolder lookaheads = new();
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, new DataColumnSidecarPool(), clock: chain.ClockAtSlot(chain.Block.Message!.Slot), forkChoiceSnapshots: snapshots, proposerLookaheads: lookaheads);
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);
        importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);
        importer.ComputeHead();
        ColumnGossipRouter router = new(chain.Spec, chain.ClockAtSlot(childSlot), LimboLogs.Instance, forkChoice: snapshots, pubkeys: chain.Pubkeys, proposerLookahead: lookaheads);
        router.Start(_ => Substitute.For<ITopic>(), [0, 0, 0, 0], [column]);
        // The fixture's lookahead is all zeros, so validator 0 is the expected proposer of the child's slot.
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, childSlot, proposerIndex: expectedProposer ? 0UL : 1UL);
        sidecar.SignedBlockHeader!.Message!.ParentRoot = chain.BlockRoot;

        MessageValidity verdict = router.Handle(column, gloasTopic: false, Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar)));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        // The expected proposer's unsigned header gets past the lookahead to the signature check.
        Assert.That(verdict, Is.EqualTo(MessageValidity.Rejected));
        Assert.That(router.GetDropCount(expectedProposer ? ColumnGossipDropReason.InvalidHeaderSignature : ColumnGossipDropReason.UnexpectedProposer), Is.EqualTo(1));
        Assert.That(router.GetDropCount(ColumnGossipDropReason.ProposerNotVerifiable), Is.Zero, "the imported head's branch is covered");
    }

    [TestCase(0xD, 32UL, 0xB, TestName = "a block at the lookahead's first slot is walked past")]
    [TestCase(0xD, 33UL, 0xC, TestName = "the latest block before the first slot is the dependent root")]
    [TestCase(0xD, 41UL, 0xD, TestName = "a block before the first slot is its own dependent root")]
    [TestCase(0xE, 32UL, 0xB, TestName = "a fork shares the dependent root of the block it branches from")]
    [TestCase(0xD, 10UL, 0xA, TestName = "a first slot at or below the tree root resolves to the tree root")]
    [TestCase(0xF, 32UL, null, TestName = "a block fork choice does not hold has no dependent root")]
    public void The_dependent_root_is_the_latest_block_before_the_lookahead(int from, ulong startSlot, int? expected)
    {
        static Hash256 Root(int id) => new([.. Enumerable.Repeat((byte)id, Hash256.Size)]);
        static ForkChoiceSnapshotNode Node(ulong slot, int id, int? parent) =>
            new(slot, Root(id), parent is { } p ? Root(p) : null, 0, 0, 0, ExecutionStatus.Valid, Hash256.Zero);

        ForkChoiceSnapshotNode[] nodes = [Node(10, 0xA, null), Node(20, 0xB, 0xA), Node(32, 0xC, 0xB), Node(35, 0xE, 0xB), Node(40, 0xD, 0xC)];

        Assert.That(ProposerLookaheadSnapshot.FindDependentRoot(nodes, Root(from), startSlot), Is.EqualTo(expected is { } id ? Root(id) : null));
    }

    [Test]
    public void Factory_built_importer_measures_the_window_against_the_clock_it_is_given()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        SlotClock pastTheWindow = chain.ClockAtEpoch(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1);
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, new DataColumnSidecarPool(), pastTheWindow);
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "without discovery there is no identity, so only the window can admit the block: the factory must hand the rule this clock");
    }

    private static void Hold(DataColumnSidecarPool pool, ImportableBlobBlock chain, IEnumerable<ulong> columns)
    {
        foreach (ulong column in columns)
        {
            pool.Add(chain.BlockRoot, chain.Block.Message!.Slot, chain.Columns[(int)column]);
        }
    }

    [TestCase(ExecutionStatus.Optimistic, false)]
    [TestCase(ExecutionStatus.Valid, true)]
    public void Import_takes_the_verdict_from_its_own_engine_call(ExecutionStatus verdict, bool sealedAgainstInvalidation)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        ScriptedPayloadEngine engine = new(ExecutionStatus.Valid, verdict);
        engine.NotifyNewPayload(chain.Block.Message!.Body!);
        BlockImporter importer = CreateImporter(chain, custody, pool, engine: engine);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        IResolveConstraint invalidation = sealedAgainstInvalidation
            ? Throws.TypeOf<ProtoArrayException>()
            : Throws.Nothing;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(() => importer.OnInvalidExecutionPayload(chain.BlockRoot, null), invalidation,
            "a block admitted as valid is sealed against invalidation; one admitted optimistically must stay invalidatable");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Block_whose_engine_call_fails_is_deferred_and_stays_importable(bool verifySignatures)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool, engine: UnavailableThenValidEngine());

        BlockImportResult deferred = importer.Import(chain.Block, chain.BlockRoot, verifySignatures);
        bool knownWhileDeferred = importer.IsKnown(chain.BlockRoot);
        BlockImportResult retried = importer.Import(chain.Block, chain.BlockRoot, verifySignatures);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deferred, Is.EqualTo(BlockImportResult.EngineUnavailable), "an unevaluated payload is not an invalid block");
        Assert.That(knownWhileDeferred, Is.False, "nothing may be recorded for a block the execution layer never saw");
        Assert.That(retried, Is.EqualTo(BlockImportResult.Imported), "the same block must import once the engine answers again");
    }

    /// <summary>The spy engine proves availability is checked before newPayload; the returned label alone cannot prove ordering.</summary>
    [Test]
    public void Blob_block_missing_columns_never_calls_the_engine()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custody.CustodyColumns[0];
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        ValidPayloadEngine engine = new();
        BlockImporter importer = CreateImporter(chain, custody, pool, engine: engine);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(engine.HasAnsweredNewPayload, Is.False, "the engine must not be consulted for a block that cannot be recorded anyway");
    }

    [Test]
    public void Blob_block_missing_columns_imports_once_the_missing_column_is_pooled()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custody.CustodyColumns[0];
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult deferred = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);
        bool knownWhileDeferred = importer.IsKnown(chain.BlockRoot);
        Hold(pool, chain, [missing]);
        BlockImportResult retried = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deferred, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(knownWhileDeferred, Is.False, "nothing may be recorded while a column is still missing");
        Assert.That(retried, Is.EqualTo(BlockImportResult.Imported), "the same block must import once the missing column arrives");
    }

    [Test]
    public void Body_attester_slashing_moves_the_head_off_the_equivocators_branch()
    {
        // Sealed by the full hasher, so each import checks the importer's incremental root against an independent one.
        UnsignedChain chain = UnsignedChain.Create(hasher: new FullBeaconStateHasher());
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        // Past every block's slot, so none is timely and no proposer boost confounds the weights.
        importer.OnSlotTick(8);
        UnsignedChain.Equivocation scenario = chain.BuildEquivocation();
        BlockImportResult[] imported =
        [
            importer.Import(scenario.A.Block, scenario.A.Root, verifySignatures: false),
            importer.Import(scenario.B.Block, scenario.B.Root, verifySignatures: false),
            importer.Import(scenario.Voted.Block, scenario.Voted.Root, verifySignatures: false),
        ];
        Hash256 headBeforeSlashing = importer.ComputeHead().HeadRoot;

        BlockImportResult slashingImported = importer.Import(scenario.Slashing.Block, scenario.Slashing.Root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(imported, Is.All.EqualTo(BlockImportResult.Imported));
        Assert.That(headBeforeSlashing, Is.EqualTo(scenario.Voted.Root), "two votes on A's branch outweigh one on B");
        Assert.That(slashingImported, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.ComputeHead().HeadRoot, Is.EqualTo(scenario.B.Root), "the slashed validators' votes no longer count, so B's lone vote wins");
        Assert.That(importer.LineageRoot, Is.EqualTo(scenario.B.Root), "a head on a competing branch is a reorg the lineage follows");
    }

    [Test]
    public void Head_falling_back_to_an_ancestor_leaves_the_lineage_on_the_chain_being_extended()
    {
        UnsignedChain chain = UnsignedChain.Create();
        UnsignedChain.ChainBlock anchor = chain.Extend(chain.AnchorRoot, slot: 3 * Presets.SlotsPerEpoch, payloadHashByte: 0x60);
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(),
            availability: ReplayedBlockAvailability.Instance, anchor: anchor);
        UnsignedChain.ChainBlock a = chain.Extend(anchor.Root, anchor.Block.Message!.Slot + 1, payloadHashByte: 0x61);
        UnsignedChain.ChainBlock b = chain.Extend(a.Root, a.Block.Message!.Slot + 1, payloadHashByte: 0x62);
        UnsignedChain.ChainBlock c = chain.Extend(b.Root, b.Block.Message!.Slot + 1, payloadHashByte: 0x63);
        importer.OnSlotTick(c.Block.Message!.Slot);
        Assert.That(importer.Import(a.Block, a.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        Assert.That(importer.Import(b.Block, b.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");

        Hash256 head = importer.ComputeHead().HeadRoot;
        Hash256? lineageAfterHeadStep = importer.LineageRoot;
        BlockImportResult extended = importer.Import(c.Block, c.Root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head, Is.EqualTo(anchor.Root), "fixture bug: the blocks above the anchor must fail the voting-source check, as they do after checkpoint sync");
        Assert.That(lineageAfterHeadStep, Is.EqualTo(b.Root), "a head that fell back to an ancestor is not a fork to move the lineage to");
        Assert.That(extended, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.LineageRoot, Is.EqualTo(c.Root), "the next block must import onto the lineage, not onto a copy of its parent's state");
    }

    [Test]
    public void Head_falling_back_to_an_ancestor_after_invalidation_moves_the_lineage_to_it()
    {
        // Sealed by the full hasher, so each import checks the importer's incremental root against an independent one.
        UnsignedChain chain = UnsignedChain.Create(hasher: new FullBeaconStateHasher());
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(),
            engine: new ScriptedPayloadEngine(ExecutionStatus.Optimistic, ExecutionStatus.Optimistic, ExecutionStatus.Optimistic));
        UnsignedChain.ChainBlock ancestor = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0x70);
        UnsignedChain.ChainBlock invalid = chain.Extend(ancestor.Root, slot: Presets.SlotsPerEpoch, payloadHashByte: 0x71);
        UnsignedChain.ChainBlock invalidChild = chain.Extend(invalid.Root, slot: Presets.SlotsPerEpoch + 1, payloadHashByte: 0x72);
        importer.OnSlotTick(invalidChild.Block.Message!.Slot);
        Assert.That(importer.Import(ancestor.Block, ancestor.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        Assert.That(importer.Import(invalid.Block, invalid.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        Assert.That(importer.Import(invalidChild.Block, invalidChild.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");

        importer.OnInvalidExecutionPayload(invalid.Root, null);
        Hash256 head = importer.ComputeHead().HeadRoot;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head, Is.EqualTo(ancestor.Root), "fixture bug: invalidation must roll the head back to the ancestor");
        Assert.That(importer.LineageRoot, Is.EqualTo(ancestor.Root), "the lineage must leave the invalid branch for the head");
    }

    [Test]
    public void Branch_built_on_a_mid_epoch_parent_imports_and_takes_the_head([Values] bool gossip, [Values] bool stateEvicted)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ManualTimestamper timestamper = new(DateTime.UnixEpoch);
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        store.PutState(chain.AnchorRoot, BeaconStateFulu.Encode(chain.Anchor.AnchorState));
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), warnings, importClock: new SlotClock(chain.Spec, timestamper), store: store);
        UnsignedChain.ChainBlock first = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0x81, signed: gossip);
        UnsignedChain.ChainBlock parent = chain.Extend(first.Root, slot: 2, payloadHashByte: 0x82, signed: gossip);
        UnsignedChain.ChainBlock late = chain.Extend(parent.Root, slot: 3, payloadHashByte: 0x83, signed: gossip);
        UnsignedChain.ChainBlock b1 = chain.Extend(parent.Root, slot: 4, payloadHashByte: 0x84, signed: gossip);
        UnsignedChain.ChainBlock[] siblings = stateEvicted
            ? [.. Enumerable.Range(0, 8).Select(i => chain.Extend(parent.Root, slot: 4, payloadHashByte: (byte)(0x90 + i), signed: gossip))]
            : [];
        UnsignedChain.ChainBlock b2 = chain.Extend(b1.Root, slot: 5, payloadHashByte: 0x85, signed: gossip);
        UnsignedChain.ChainBlock b3 = chain.Extend(b2.Root, slot: 6, payloadHashByte: 0x86, signed: gossip);

        BlockImportResult ImportAt(UnsignedChain.ChainBlock block, long msIntoSlot)
        {
            timestamper.Set(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + block.Block.Message!.Slot * chain.Spec.SecondsPerSlot).AddMilliseconds(msIntoSlot));
            return importer.Import(block.Block, block.Root, verifySignatures: gossip);
        }

        Assert.That(new[] { first, parent, late }.Select(b => ImportAt(b, 1000)), Is.All.EqualTo(BlockImportResult.Imported), "fixture bug");
        Assert.That((importer.ComputeHead().HeadRoot, importer.LineageRoot), Is.EqualTo((late.Root, (Hash256?)late.Root)), "fixture bug");

        BlockImportResult b1Imported = ImportAt(b1, 1000);
        BlockImportResult[] siblingsImported = [.. siblings.Select(s => ImportAt(s, 5000))];
        Hash256 headAfterB1 = importer.ComputeHead().HeadRoot;
        Hash256? lineageAfterB1 = importer.LineageRoot;
        BlockImportResult[] restImported = [ImportAt(b2, 1000), ImportAt(b3, 1000)];
        Hash256 headAfterB3 = importer.ComputeHead().HeadRoot;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(b1Imported, Is.EqualTo(BlockImportResult.Imported), "the parent's post-state must outlive the lineage moving to its other child");
        Assert.That(siblingsImported, Is.All.EqualTo(BlockImportResult.Imported));
        Assert.That((headAfterB1, lineageAfterB1), Is.EqualTo((b1.Root, (Hash256?)b1.Root)), "the boosted block is the head, and the lineage follows it");
        Assert.That(restImported, Is.All.EqualTo(BlockImportResult.Imported));
        Assert.That((headAfterB3, importer.LineageRoot), Is.EqualTo((b3.Root, (Hash256?)b3.Root)));
        Assert.That(warnings.Messages, Has.None.Contains("is no longer retained"), "no block may be refused for a parent post-state fork choice still needs");
    }

    [Test]
    public void Regeneration_uses_nearest_held_ancestor_without_mutating_it_and_retains_result([Values] bool persisted, [Values] bool hasLineage)
    {
        UnsignedChain chain = UnsignedChain.Create();
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        UnsignedChain.ChainBlock first = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0x81);
        UnsignedChain.ChainBlock second = chain.Extend(first.Root, slot: 2, payloadHashByte: 0x82);
        UnsignedChain.ChainBlock third = chain.Extend(second.Root, slot: 3, payloadHashByte: 0x83);
        store.PutBlock(second.Root, second.Block);
        store.PutBlock(third.Root, third.Block);
        PostStateCache states = new(store, chain.Spec, hasLineage ? chain.AnchorRoot : null, hasLineage ? chain.Anchor.AnchorState : null,
            pubkeys: chain.Anchor.Pubkeys, ancestors: root => root == third.Root ? [third.Root, second.Root, first.Root, chain.AnchorRoot] : []);
        List<Hash256> filler = [];
        if (persisted)
        {
            store.PutState(first.Root, BeaconStateFulu.Encode(first.PostState));
        }
        else
        {
            states.Retain(first.Root, first.PostState);
            const int RetainedStateCount = 8;
            for (int i = 1; i < RetainedStateCount; i++)
            {
                Hash256 root = Keccak.Compute([(byte)i]);
                states.Retain(root, chain.Anchor.AnchorState);
                filler.Add(root);
            }
        }

        BeaconStateFulu? coldCopy = states.CopyBlockState(third.Root);
        Assert.That(coldCopy, Is.Not.Null, "a cold copy regenerates from the nearest held or persisted ancestor");
        Assert.That(SszRoots.HashTreeRoot(coldCopy!), Is.EqualTo(third.Block.Message!.StateRoot));
        Assert.That(states.GetHeldBlockState(third.Root), Is.Null, "the cold copy is the caller's alone");
        BeaconStateFulu? regenerated = states.GetBlockState(third.Root);
        Assert.That(regenerated, Is.Not.Null);
        Assert.That(regenerated, Is.Not.SameAs(coldCopy), "an import regenerates its own state after a caller's cold copy");
        Assert.That(SszRoots.HashTreeRoot(regenerated!), Is.EqualTo(third.Block.Message!.StateRoot));
        Assert.That(SszRoots.HashTreeRoot(first.PostState), Is.EqualTo(first.Block.Message!.StateRoot));
        Assert.That(SszRoots.HashTreeRoot(chain.Anchor.AnchorState), Is.EqualTo(chain.Anchor.AnchorBlock.Message!.StateRoot));
        if (!persisted)
        {
            Assert.That(states.GetHeldBlockState(first.Root), Is.SameAs(first.PostState), "regeneration leaves the retained parent held");
            Assert.That(filler.Select(states.GetHeldBlockState), Has.All.SameAs(chain.Anchor.AnchorState), "regeneration cannot evict a live retained state");
        }
        Assert.That(states.GetHeldBlockState(third.Root), Is.SameAs(regenerated));
        store.DeleteBlock(second.Root);
        store.DeleteBlock(third.Root);
        Assert.That(states.GetBlockState(third.Root), Is.SameAs(regenerated));
        BeaconStateFulu copy = states.CopyBlockState(third.Root)!;
        Assert.That(copy, Is.Not.SameAs(regenerated));
        copy.Slot++;
        Assert.That(regenerated!.Slot, Is.EqualTo(third.PostState.Slot));
    }

    [TestCase("unknown")]
    [TestCase("gloas")]
    [TestCase("ancestor")]
    [TestCase("block")]
    [TestCase("stateRoot")]
    public void Regeneration_refuses_unresolvable_or_invalid_replay_without_retaining_partial_state(string missing)
    {
        UnsignedChain chain = UnsignedChain.Create();
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        UnsignedChain.ChainBlock first = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0x81);
        UnsignedChain.ChainBlock second = chain.Extend(first.Root, slot: 2, payloadHashByte: 0x82);
        Hash256 expectedAnchor = SszRoots.HashTreeRoot(chain.Anchor.AnchorState);
        store.PutBlock(first.Root, first.Block);
        if (missing == "stateRoot") second.Block.Message!.StateRoot = UnknownBlockRoot;
        if (missing != "block") store.PutBlock(second.Root, second.Block);
        PostStateCache states = new(store, chain.Spec, missing == "ancestor" ? null : chain.AnchorRoot, chain.Anchor.AnchorState,
            isGloasBlock: _ => missing == "gloas", pubkeys: chain.Anchor.Pubkeys,
            ancestors: root => missing == "unknown" ? [] : [second.Root, first.Root, chain.AnchorRoot]);

        Assert.That(states.GetBlockState(second.Root), Is.Null);
        Assert.That(states.GetBlockState(second.Root), Is.Null);
        Assert.That(SszRoots.HashTreeRoot(chain.Anchor.AnchorState), Is.EqualTo(expectedAnchor));
    }

    [Test]
    public void Regeneration_replays_at_most_one_epoch_of_stored_blocks([Values] bool beyondBound)
    {
        UnsignedChain chain = UnsignedChain.Create();
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        int count = (int)chain.Spec.SlotsPerEpoch + (beyondBound ? 1 : 0);
        List<UnsignedChain.ChainBlock> blocks = [];
        Hash256 parentRoot = chain.AnchorRoot;
        for (int i = 1; i <= count; i++)
        {
            UnsignedChain.ChainBlock block = chain.Extend(parentRoot, slot: (ulong)i, payloadHashByte: (byte)i);
            store.PutBlock(block.Root, block.Block);
            blocks.Add(block);
            parentRoot = block.Root;
        }

        Hash256[] ancestry = [.. blocks.Select(static b => b.Root).Reverse(), chain.AnchorRoot];
        int walked = 0;
        IEnumerable<Hash256> Ancestors(Hash256 root)
        {
            foreach (Hash256 ancestor in ancestry.SkipWhile(r => r != root))
            {
                walked++;
                yield return ancestor;
            }
        }

        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        PostStateCache states = new(store, chain.Spec, chain.AnchorRoot, chain.Anchor.AnchorState, logManager: new OneLoggerLogManager(new ILogger(warnings)),
            pubkeys: chain.Anchor.Pubkeys, ancestors: Ancestors);

        long started = Stopwatch.GetTimestamp();
        BeaconStateFulu? regenerated = states.GetBlockState(blocks[^1].Root);
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!beyondBound) TestContext.Out.WriteLine($"Fulu regeneration: {count} blocks in {elapsedMs:F1} ms, {elapsedMs / count:F2} ms per block, {chain.Anchor.AnchorState.Validators!.Length} validators");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(regenerated is null ? null : SszRoots.HashTreeRoot(regenerated), Is.EqualTo(beyondBound ? null : blocks[^1].Block.Message!.StateRoot));
        Assert.That(walked, Is.LessThanOrEqualTo((int)chain.Spec.SlotsPerEpoch + 1), "the ancestor walk stops at the bound, before reading any more stored states");
        Assert.That(warnings.Messages, beyondBound ? Has.One.Contains($"no ancestor state is held within {chain.Spec.SlotsPerEpoch} blocks") : Is.Empty);
    }

    [Test]
    public void Forged_children_of_evicted_blocks_do_not_spend_the_regeneration_budget()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + 8 * chain.Spec.SecondsPerSlot + 1));
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        store.PutState(chain.AnchorRoot, BeaconStateFulu.Encode(chain.Anchor.AnchorState));
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), importClock: new SlotClock(chain.Spec, timestamper), store: store);
        List<UnsignedChain.ChainBlock> lineage = [];
        Hash256 tip = chain.AnchorRoot;
        for (ulong slot = 1; slot <= 6; slot++)
        {
            lineage.Add(chain.Extend(tip, slot, payloadHashByte: (byte)(0x80 + slot), signed: true));
            tip = lineage[^1].Root;
        }

        UnsignedChain.ChainBlock sibling = chain.Extend(lineage[3].Root, slot: 6, payloadHashByte: 0x90, signed: true);
        BlockImportResult[] fixture = [.. lineage.Select(b => importer.Import(b.Block, b.Root, verifySignatures: false))];

        BlockImportResult[] forged = [.. new[] { 1, 2 }.Select(i =>
        {
            BeaconBlock real = lineage[i + 1].Block.Message!;
            BeaconBlock message = new() { Slot = real.Slot, ProposerIndex = real.ProposerIndex, ParentRoot = real.ParentRoot, StateRoot = Keccak.Compute(BitConverter.GetBytes(i)), Body = real.Body };
            return importer.Import(new SignedBeaconBlock { Message = message, Signature = lineage[i + 1].Block.Signature }, SszRoots.HashTreeRoot(message), verifySignatures: true);
        })];
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        bool forgedParentRegenerated = states.GetHeldBlockState(lineage[1].Root) is not null || states.GetHeldBlockState(lineage[2].Root) is not null;
        BlockImportResult result = importer.Import(sibling.Block, sibling.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture, Is.All.EqualTo(BlockImportResult.Imported), "fixture bug");
        Assert.That(forged, Is.All.EqualTo(BlockImportResult.Invalid));
        Assert.That(forgedParentRegenerated, Is.False, "no state is regenerated for a block whose proposer signature fails");
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the real block on an evicted parent still has the slot's regeneration budget");
    }

    [Test]
    public void Checkpoint_block_state_outlives_branch_churn_so_regeneration_stays_within_one_epoch([Values] bool onBranch, [Values] bool startSlotEmpty, [Values] bool gossip)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ulong epochStart = chain.Spec.SlotsPerEpoch;
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + (epochStart + 16) * chain.Spec.SecondsPerSlot));
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db);
        store.PutState(chain.AnchorRoot, BeaconStateFulu.Encode(chain.Anchor.AnchorState));
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), warnings, importClock: new SlotClock(chain.Spec, timestamper), store: store);
        byte fill = 0;
        UnsignedChain.ChainBlock Extend(Hash256 parentRoot, ulong slot) => chain.Extend(parentRoot, slot, payloadHashByte: ++fill, signed: gossip);
        BlockImportResult Import(UnsignedChain.ChainBlock block) => importer.Import(block.Block, block.Root, verifySignatures: gossip);
        List<BlockImportResult> fixture = [];
        // Built before any import: a trusted import advances the anchor's state in place as the lineage.
        UnsignedChain.ChainBlock[] churn = [.. Enumerable.Range(0, 9).Select(_ => Extend(chain.AnchorRoot, epochStart + 5))];

        Hash256 forkPoint = chain.AnchorRoot;
        for (ulong slot = 1; slot <= epochStart - 3; slot++)
        {
            UnsignedChain.ChainBlock block = Extend(forkPoint, slot);
            fixture.Add(Import(block));
            forkPoint = block.Root;
        }

        if (onBranch)
        {
            Hash256 lineageTip = forkPoint;
            for (ulong slot = epochStart - 2; slot <= epochStart + 8; slot++)
            {
                UnsignedChain.ChainBlock block = Extend(lineageTip, slot);
                fixture.Add(Import(block));
                lineageTip = block.Root;
            }
        }

        Dictionary<ulong, UnsignedChain.ChainBlock> segment = [];
        Hash256 tip = forkPoint;
        for (ulong slot = epochStart - 2; slot <= epochStart + 4; slot++)
        {
            if (startSlotEmpty && slot == epochStart)
            {
                continue;
            }

            segment[slot] = Extend(tip, slot);
            fixture.Add(Import(segment[slot]));
            tip = segment[slot].Root;
        }

        fixture.AddRange(churn.Select(Import));

        Assert.That(fixture, Is.All.EqualTo(BlockImportResult.Imported), "fixture bug");
        BlockImportResult result = Import(Extend(segment[epochStart + 3].Root, epochStart + 5));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "a mid-epoch parent is regenerated from its epoch's checkpoint block state");
        Assert.That(warnings.Messages, Has.None.Contains("Cannot regenerate"));
    }

    [Test]
    public void Body_attestation_refused_by_fork_choice_is_tolerated_and_counted()
    {
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        UnsignedChain.ChainBlock a = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        // One vote the store accepts and one it refuses, so a counter that fires per attestation
        // rather than per refusal reports two and fails here.
        UnsignedChain.ChainBlock strayVote = chain.Extend(a.Root, slot: 2, payloadHashByte: 0xa2,
            attestations: [chain.Vote(1, a.Root), chain.Vote(1, UnknownBlockRoot)]);
        long refusedBefore = RefusedByForkChoice("body_attestation");

        BlockImportResult parent = importer.Import(a.Block, a.Root, verifySignatures: false);
        BlockImportResult result = importer.Import(strayVote.Block, strayVote.Root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(parent, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "a vote for a block we never saw is not a reason to drop a block the transition accepted");
        Assert.That(importer.IsKnown(strayVote.Root), Is.True);
        Assert.That(RefusedByForkChoice("body_attestation") - refusedBefore, Is.EqualTo(1), "a tolerated refusal must still be observable");
    }

    [Test]
    public void Body_attestation_for_a_target_of_another_shuffling_is_checked_against_its_signature()
    {
        const ulong epoch = 2;
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        UnsignedChain.ChainBlock p = chain.Extend(chain.AnchorRoot, slot: 30, payloadHashByte: 0xB0);
        UnsignedChain.ChainBlock a = chain.Extend(p.Root, slot: 31, payloadHashByte: 0xB1);
        CommitteeCache pCommittees = EpochCommittees(p, epoch);
        CommitteeCache aCommittees = EpochCommittees(a, epoch);
        ulong voteSlot = epoch * Presets.SlotsPerEpoch;
        while (pCommittees.GetBeaconCommittee(voteSlot, 0).Length != 1 || aCommittees.GetBeaconCommittee(voteSlot, 0).Length != 1
            || pCommittees.GetBeaconCommittee(voteSlot, 0)[0] == aCommittees.GetBeaconCommittee(voteSlot, 0)[0])
        {
            voteSlot++;
        }

        UnsignedChain.ChainBlock voting = chain.Extend(a.Root, slot: voteSlot + 1, payloadHashByte: 0xB2, attestations: [Vote(p.Root), Vote(a.Root)]);
        BlockImportResult[] results = [.. new[] { p, a, voting }.Select(block => importer.Import(block.Block, block.Root, verifySignatures: false))];
        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        runner.GetHead();
        ForkChoiceSnapshot snapshot = runner.Snapshot();
        ulong balance = chain.Anchor.AnchorState.Validators![0].EffectiveBalance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(voteSlot, Is.LessThan((epoch + 1) * Presets.SlotsPerEpoch - 1), "fixture bug: no slot of epoch 2 has two one-member committees of different validators");
            Assert.That(results, Is.All.EqualTo(BlockImportResult.Imported));
            Assert.That(Weight(p.Root) - Weight(a.Root), Is.Zero, "the vote for P credits nobody");
            Assert.That(Weight(a.Root), Is.EqualTo(balance), "the vote for A counts");
        }

        ulong Weight(Hash256 root) => snapshot.Nodes.Single(node => node.Root == root).Weight;

        Attestation Vote(Hash256 target) => new()
        {
            AggregationBits = new BitArray(1, true),
            Data = new AttestationData
            {
                Slot = voteSlot,
                Index = 0,
                BeaconBlockRoot = target,
                Source = chain.Anchor.AnchorState.CurrentJustifiedCheckpoint,
                Target = new Checkpoint { Epoch = epoch, Root = target },
            },
            Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
            CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
        };
    }

    private static CommitteeCache EpochCommittees(UnsignedChain.ChainBlock block, ulong epoch)
    {
        BeaconStateFulu state = block.PostState.Clone();
        SlotProcessing.ProcessSlots(state, epoch * Presets.SlotsPerEpoch, new EpochCache { Hasher = new CachedBeaconStateHasher() });
        return new EpochCache().GetCommitteeCache(state, epoch);
    }

    [Test]
    public void Body_attester_slashing_refused_by_fork_choice_is_tolerated_and_counted()
    {
        UnsignedChain chain = UnsignedChain.Create();
        (BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot) = AnchorWithQueuedValidator(chain.Anchor.AnchorState, chain.Anchor.AnchorBlock);
        ulong onboarded = (ulong)anchorState.Validators!.Length;
        AttesterSlashing slashing = chain.DoubleVote([1, onboarded], slot: 1, chain.AnchorRoot, UnknownBlockRoot);
        // The slashing block forks off the anchor, so its transition runs on a copy and the anchor's state stays the justified one.
        (SignedBeaconBlock lineage, Hash256 lineageRoot, _) = UnsignedChild(anchorState, anchorRoot, slot: 1, []);
        (SignedBeaconBlock block, Hash256 root, BeaconStateFulu postState) = UnsignedChild(anchorState, anchorRoot, slot: Presets.SlotsPerEpoch + 1, [slashing]);
        Assert.That(postState.Validators, Has.Length.EqualTo(anchorState.Validators.Length + 1), "fixture bug: the queued validator must be onboarded before the slashing block");
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(),
            availability: ReplayedBlockAvailability.Instance, anchor: new UnsignedChain.ChainBlock(anchorBlock, anchorRoot, anchorState));
        Assert.That(importer.Import(lineage, lineageRoot, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        long refusedBefore = RefusedByForkChoice("body_attester_slashing");

        BlockImportResult result = importer.Import(block, root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the transition accepted the slashing, so fork choice's refusal must not sink the block");
        Assert.That(RefusedByForkChoice("body_attester_slashing") - refusedBefore, Is.EqualTo(1), "a tolerated refusal must still be observable");
    }

    [Test]
    public void ComputeHead_publishes_the_weights_its_head_was_chosen_by()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), forkChoiceSnapshots: snapshots);
        importer.OnSlotTick(8);
        UnsignedChain.Equivocation scenario = chain.BuildEquivocation();
        foreach (UnsignedChain.ChainBlock block in new[] { scenario.A, scenario.B, scenario.Voted })
        {
            Assert.That(importer.Import(block.Block, block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        }

        Hash256 head = importer.ComputeHead().HeadRoot;
        ForkChoiceSnapshot published = snapshots.Current!;

        ulong vote = chain.Anchor.AnchorState.Validators![0].EffectiveBalance;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head, Is.EqualTo(scenario.Voted.Root));
        Assert.That(published.Nodes.Single(n => n.Root == scenario.A.Root).Weight, Is.EqualTo(2 * vote), "A carries the two slot-1 and slot-3 votes");
        Assert.That(published.Nodes.Single(n => n.Root == scenario.B.Root).Weight, Is.EqualTo(vote), "B carries the slot-5 vote");
    }

    [Test]
    public void Child_of_an_invalid_parent_is_refused_before_any_engine_call()
    {
        UnsignedChain chain = UnsignedChain.Create();
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), warnings, engine: new ScriptedPayloadEngine(ExecutionStatus.Optimistic), failedBlocks: failed);
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock child = chain.Extend(parent.Root, slot: 2, payloadHashByte: 0xa2);
        Assert.That(importer.Import(parent.Block, parent.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        importer.OnInvalidExecutionPayload(parent.Root, null);

        BlockImportResult result = importer.Import(child.Block, child.Root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(warnings.Messages, Has.Some.Contains("before its state transition").And.Contains("has an invalid execution payload"));
        Assert.That(failed.Contains(child.Root), Is.True, "column gossip must reject a sidecar whose parent the importer refused");
    }

    [Test]
    public void Invalid_new_payload_invalidates_the_optimistic_blocks_after_its_latest_valid_hash([Values(-1, 0, 1, 2)] int latestValid, [Values] bool offLineage)
    {
        UnsignedChain chain = UnsignedChain.Create();
        UnsignedChain.ChainBlock v = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0x71);
        UnsignedChain.ChainBlock a = chain.Extend(v.Root, slot: 2, payloadHashByte: 0x72);
        UnsignedChain.ChainBlock b = chain.Extend(a.Root, slot: 3, payloadHashByte: 0x73);
        UnsignedChain.ChainBlock refused = chain.Extend(b.Root, slot: 4, payloadHashByte: 0x74);
        UnsignedChain.ChainBlock side = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0x75);
        UnsignedChain.ChainBlock[] held = [v, a, b];
        ExecutionStatus[] verdicts = offLineage
            ? [ExecutionStatus.Optimistic, ExecutionStatus.Optimistic, ExecutionStatus.Optimistic, ExecutionStatus.Optimistic, ExecutionStatus.Invalid]
            : [ExecutionStatus.Optimistic, ExecutionStatus.Optimistic, ExecutionStatus.Optimistic, ExecutionStatus.Invalid];
        ScriptedPayloadEngine engine = new(verdicts)
        {
            LatestValidHash = latestValid < 0 ? Keccak.Compute("unknown payload") : held[latestValid].Block.Message!.Body!.ExecutionPayload!.BlockHash,
        };
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), engine: engine, forkChoiceSnapshots: snapshots);
        importer.OnSlotTick(refused.Block.Message!.Slot);
        if (offLineage)
        {
            Assert.That(importer.Import(side.Block, side.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        }
        foreach (UnsignedChain.ChainBlock block in held)
            Assert.That(importer.Import(block.Block, block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture: imported optimistically");
        Assert.That(importer.LineageRoot == b.Root, Is.EqualTo(!offLineage));

        BlockImportResult result = importer.Import(refused.Block, refused.Root, verifySignatures: false);
        importer.ComputeHead();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        for (int i = 0; i < held.Length; i++)
        {
            ExecutionStatus expected = latestValid >= 0 && i > latestValid ? ExecutionStatus.Invalid : ExecutionStatus.Optimistic;
            Assert.That(snapshots.Current!.Nodes.Single(n => n.Root == held[i].Root).ExecutionStatus, Is.EqualTo(expected));
        }
    }

    [TestCase(PayloadStatus.Valid, ExecutionStatus.Valid, ExecutionStatus.Valid)]
    [TestCase(PayloadStatus.Invalid, ExecutionStatus.Optimistic, ExecutionStatus.Invalid)]
    [TestCase(PayloadStatus.Syncing, ExecutionStatus.Optimistic, ExecutionStatus.Optimistic)]
    public void Forkchoice_verdict_changes_the_status_of_the_optimistic_head_chain(string status, ExecutionStatus expectedParent, ExecutionStatus expectedHead)
    {
        UnsignedChain chain = UnsignedChain.Create();
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0x81);
        UnsignedChain.ChainBlock head = chain.Extend(parent.Root, slot: 2, payloadHashByte: 0x82);
        UnsignedChain.ChainBlock side = chain.Extend(chain.AnchorRoot, slot: 2, payloadHashByte: 0x92);
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), forkChoiceSnapshots: snapshots,
            engine: new ScriptedPayloadEngine(ExecutionStatus.Optimistic, ExecutionStatus.Optimistic, ExecutionStatus.Optimistic));
        importer.OnSlotTick(2);
        foreach (UnsignedChain.ChainBlock block in new[] { parent, head, side })
            Assert.That(importer.Import(block.Block, block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture: imported optimistically");
        Hash256 parentHash = parent.Block.Message!.Body!.ExecutionPayload!.BlockHash!;

        importer.OnForkchoiceUpdated(head.Root, head.Block.Message!.Body!.ExecutionPayload!.BlockHash!, new PayloadStatusV1 { Status = status, LatestValidHash = parentHash });
        importer.ComputeHead();

        ForkChoiceSnapshotNode[] nodes = [.. snapshots.Current!.Nodes];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(nodes.Single(n => n.Root == parent.Root).ExecutionStatus, Is.EqualTo(expectedParent));
        Assert.That(nodes.Single(n => n.Root == head.Root).ExecutionStatus, Is.EqualTo(expectedHead));
        Assert.That(nodes.Single(n => n.Root == side.Root).ExecutionStatus, Is.EqualTo(ExecutionStatus.Optimistic));
    }

    [Test]
    public void Block_refused_by_the_state_transition_is_recorded_as_failed()
    {
        UnsignedChain chain = UnsignedChain.Create();
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), failedBlocks: failed);
        UnsignedChain.ChainBlock valid = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock broken = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xb1);
        broken.Block.Message!.StateRoot = Keccak.Compute("wrong state root");
        Hash256 brokenRoot = SszRoots.HashTreeRoot(broken.Block.Message);

        BlockImportResult validResult = importer.Import(valid.Block, valid.Root, verifySignatures: false);
        BlockImportResult brokenResult = importer.Import(broken.Block, brokenRoot, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((validResult, brokenResult), Is.EqualTo((BlockImportResult.Imported, BlockImportResult.Invalid)));
        Assert.That(failed.Contains(brokenRoot), Is.True);
        Assert.That(failed.Contains(valid.Root), Is.False, "an imported block is not a failed one");
        Assert.That(failed.Count, Is.EqualTo(1));
    }

    [Test]
    public void Block_with_only_a_bad_proposer_signature_is_not_recorded_as_failed()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), warnings, failedBlocks: failed);
        BlsSignature genuine = chain.Block.Signature;
        chain.Block.Signature = new BlsSignature(SignatureSets.G2PointAtInfinity);

        BlockImportResult forged = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);
        chain.Block.Signature = genuine;
        BlockImportResult honest = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((forged, honest), Is.EqualTo((BlockImportResult.Invalid, BlockImportResult.Imported)));
        Assert.That(warnings.Messages, Has.Some.Contains("Invalid proposer signature"), "fixture: the forged copy fails on its signature");
        Assert.That(failed.Contains(chain.BlockRoot), Is.False);
    }

    [TestCase(0UL, false, TestName = "Block_at_the_finalized_slot_is_not_recorded_as_failed")]
    [TestCase(2UL, true, TestName = "Block_not_after_its_parents_slot_is_recorded_as_failed")]
    public void Refusal_before_the_state_transition_is_recorded_only_for_a_validation_failure(ulong slot, bool recorded)
    {
        UnsignedChain chain = UnsignedChain.Create();
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), failedBlocks: failed);
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, slot: 2, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock child = chain.Extend(parent.Root, slot: 3, payloadHashByte: 0xa2);
        Assert.That(importer.Import(parent.Block, parent.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        child.Block.Message!.Slot = slot;
        Hash256 root = SszRoots.HashTreeRoot(child.Block.Message);

        BlockImportResult result = importer.Import(child.Block, root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(importer.LastRefusal, Is.EqualTo(ImportRefusal.None), "a slot not after the parent's is invalid data, whatever the finalized slot");
        Assert.That(failed.Contains(root), Is.EqualTo(recorded));
    }

    [Test]
    public void Block_off_the_finalized_chain_is_recorded_as_failed()
    {
        UnsignedChain chain = UnsignedChain.Create();
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), warnings, failedBlocks: failed);
        UnsignedChain.ChainBlock first = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        Assert.That(importer.Import(first.Block, first.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        // A trusted import runs in place on the anchor state object, so this edits the importer's post-state of the first block too.
        foreach (BeaconStateFulu state in (BeaconStateFulu[])[chain.Anchor.AnchorState, first.PostState])
        {
            // process_slot would otherwise seal the edited state's root into the header, and the child's parent root would miss the first block.
            state.LatestBlockHeader!.StateRoot = first.Block.Message!.StateRoot;
            state.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 1, Root = chain.AnchorRoot };
            state.FinalizedCheckpoint = new Checkpoint { Epoch = 1, Root = chain.AnchorRoot };
        }

        UnsignedChain.ChainBlock offChain = chain.Extend(first.Root, slot: Presets.SlotsPerEpoch, payloadHashByte: 0xa2);
        UnsignedChain.ChainBlock child = chain.Extend(offChain.Root, slot: Presets.SlotsPerEpoch + 1, payloadHashByte: 0xa3);
        Assert.That(importer.Import(offChain.Block, offChain.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");

        BlockImportResult result = importer.Import(child.Block, child.Root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(warnings.Messages, Has.Some.Contains("does not descend from the finalized checkpoint"), "fixture: refused for its ancestry");
        Assert.That(importer.LastRefusal, Is.EqualTo(ImportRefusal.LocalAdmission), "descent from this node's finalized checkpoint says nothing of the block's data");
        Assert.That(failed.Contains(child.Root), Is.True);
    }

    [TestCase(Presets.SlotsPerEpoch + 1, true, TestName = "Block_refused_after_the_tick_finalized_a_conflicting_branch_is_recorded_as_failed")]
    [TestCase(Presets.SlotsPerEpoch, false, TestName = "Block_refused_after_the_tick_at_the_finalized_slot_is_not_recorded_as_failed")]
    public void Refusal_by_fork_choice_after_the_tick_is_recorded_only_for_a_validation_failure(ulong slot, bool recorded)
    {
        UnsignedChain chain = UnsignedChain.Create();
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        FailedBlockRoots failed = new();
        ManualTimestamper time = new(TickFinalityFixture.SlotStart(chain.Spec, 2));
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), warnings, importClock: new SlotClock(chain.Spec, time), failedBlocks: failed);
        UnsignedChain.ChainBlock offChain = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock child = chain.Extend(offChain.Root, slot, payloadHashByte: 0xa2);
        Assert.That(importer.Import(offChain.Block, offChain.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        TickFinalityFixture.SetUnrealizedFinality(importer, new CheckpointRef(1, chain.AnchorRoot));
        time.Set(TickFinalityFixture.SlotStart(chain.Spec, slot));

        BlockImportResult result = importer.Import(child.Block, child.Root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(warnings.Messages, Has.Some.Contains("rejected by fork choice"), "fixture: the pre-transition checks passed and on_block refused");
        Assert.That(importer.LastRefusal, Is.EqualTo(ImportRefusal.LocalAdmission), "on_block refused it against this node's own store");
        Assert.That(failed.Contains(child.Root), Is.EqualTo(recorded));
    }

    // Spec Fulu on_block asserts availability before state_transition; the saved verdict belongs to that root.
    [Test]
    public void Block_is_checked_for_data_availability_once([Values] bool matchingRoot)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        FailedBlockRoots failed = new();
        AvailableOnce availability = new();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), failedBlocks: failed, availability: availability);

        BlockImportResult result = importer.Import(chain.Block, matchingRoot ? chain.BlockRoot : Hash256.Zero, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(availability.Calls, Is.EqualTo(1), "neither the importer nor fork choice asks again");
        Assert.That(result, Is.EqualTo(matchingRoot ? BlockImportResult.Imported : BlockImportResult.Invalid));
        Assert.That(failed.Contains(chain.BlockRoot), Is.False);
    }

    [Test]
    public void Gloas_invalid_gossip_fields_record_failure_and_refuse_dependents([Values] bool wrongBidParent, [Values] bool verifySignatures)
    {
        const ulong forkSlot = 32;
        SignedGloasChain chain = new();
        FailedBlockRoots failed = new();
        SlotClock clock = new(chain.Spec, new ManualTimestamper(TickFinalityFixture.SlotStart(chain.Spec, forkSlot + 1)));
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: clock, failedBlocks: failed);
        SignedGloasChain.Block broken = chain.Next(null, forkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = chain.Next(broken, forkSlot + 1, full: false, 0xA2);
        if (wrongBidParent)
            broken.Bid.ParentBlockRoot = UnknownBlockRoot;
        else
        {
            int count = (int)(chain.Spec.GetBlobParameters(chain.Spec.GetEpoch(forkSlot))?.MaxBlobsPerBlock ?? chain.Spec.MaxBlobsPerBlockElectra) + 1;
            broken.Bid.BlobKzgCommitments = Enumerable.Repeat(SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength]), count).ToArray();
        }
        Hash256 brokenRoot = broken.Forked.ComputeMessageRoot();
        broken.Signed.Signature = GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey((int)broken.Signed.Message!.ProposerIndex),
            Domains.ComputeSigningRoot(brokenRoot, broken.PostState.GetDomain(DomainType.BeaconProposer, chain.Spec.GetEpoch(forkSlot))));
        child.Signed.Message!.ParentRoot = brokenRoot;
        child.Bid.ParentBlockRoot = brokenRoot;
        broken.Envelope.Message!.BeaconBlockRoot = brokenRoot;
        GossipRouter router = new(chain.Spec, clock, LimboLogs.Instance, failedBlocks: failed);

        BlockImportResult result = importer.Import(broken.Forked, brokenRoot, verifySignatures);
        BlockImportResult childResult = importer.Import(child.Forked, child.Forked.ComputeMessageRoot(), verifySignatures);
        MessageValidity envelopeVerdict = router.Handle(GossipTopics.ExecutionPayload, gloasTopic: true,
            Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(broken.Envelope)));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(failed.Contains(brokenRoot), Is.True, "an invalid bid is committed by the block root");
        Assert.That(childResult, Is.EqualTo(BlockImportResult.UnknownParent), "an invalid block cannot become an imported parent");
        Assert.That(envelopeVerdict, Is.EqualTo(MessageValidity.Rejected), "the failed root must stop its envelope before import");
    }

    [Test]
    public void Gloas_block_refused_by_the_state_transition_is_recorded_as_failed()
    {
        const ulong forkSlot = 32;
        SignedGloasChain chain = new();
        FailedBlockRoots failed = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), failedBlocks: failed);
        SignedGloasChain.Block broken = chain.Next(null, forkSlot, full: false, 0xA1);
        broken.Signed.Message!.StateRoot = Keccak.Compute("wrong state root");
        Hash256 brokenRoot = SszRoots.HashTreeRoot(broken.Signed.Message);

        BlockImportResult result = importer.Import(broken.Forked, brokenRoot, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(failed.Contains(brokenRoot), Is.True);
    }

    [Test]
    public void Gloas_block_refused_after_the_tick_finalized_a_conflicting_branch_is_recorded_as_failed()
    {
        const ulong forkSlot = 32;
        SignedGloasChain chain = new();
        TestLogRecorder warnings = new(TestLogLevels.Warn | TestLogLevels.Error);
        FailedBlockRoots failed = new();
        ManualTimestamper time = new(TickFinalityFixture.SlotStart(chain.Spec, forkSlot + 1));
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(warnings)), clock: new SlotClock(chain.Spec, time), failedBlocks: failed);
        SignedGloasChain.Block finalized = chain.Next(null, forkSlot, full: false, 0xA1);
        SignedGloasChain.Block otherBranch = chain.Next(null, forkSlot + 1, full: false, 0xA2);
        SignedGloasChain.Block child = chain.Next(otherBranch, 2 * forkSlot, full: false, 0xA3);
        foreach (SignedGloasChain.Block block in (SignedGloasChain.Block[])[finalized, otherBranch])
        {
            Assert.That(importer.Import(block.Forked, block.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported), "fixture bug");
        }

        TickFinalityFixture.SetUnrealizedFinality(importer, new CheckpointRef(1, finalized.Root));
        time.Set(TickFinalityFixture.SlotStart(chain.Spec, 2 * forkSlot));

        BlockImportResult result = importer.Import(child.Forked, child.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(warnings.Messages, Has.Some.Contains("rejected by fork choice"), "fixture: the pre-transition checks passed and on_block refused");
        Assert.That(importer.LastRefusal, Is.EqualTo(ImportRefusal.LocalAdmission), "on_block refused it against this node's own store");
        Assert.That(failed.Contains(child.Root), Is.True);
    }

    [Test]
    public void Gloas_block_with_only_a_bad_proposer_signature_is_not_recorded_as_failed()
    {
        SignedGloasChain chain = new();
        FailedBlockRoots failed = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), failedBlocks: failed);
        SignedGloasChain.Block block = chain.Next(null, 32, full: false, 0xA1);
        BlsSignature genuine = block.Signed.Signature;
        block.Signed.Signature = new BlsSignature(SignatureSets.G2PointAtInfinity);

        BlockImportResult forged = importer.Import(block.Forked, block.Root, verifySignatures: true);
        block.Signed.Signature = genuine;
        BlockImportResult honest = importer.Import(block.Forked, block.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((forged, honest), Is.EqualTo((BlockImportResult.Invalid, BlockImportResult.Imported)));
        Assert.That(failed.Contains(block.Root), Is.False);
    }

    [Test]
    public void A_persisted_anchor_imports_its_child_after_resuming(
        [Values] bool gloas, [Values(0UL, 1UL, 31UL)] ulong slotInEpoch)
    {
        BeaconChainSpec spec;
        ForkedSignedBeaconBlock anchor;
        ForkedSignedBeaconBlock child;
        byte[] stateSsz;
        PubkeyCache pubkeys = new();
        if (gloas)
        {
            SignedGloasChain chain = new();
            SignedGloasChain.Block first = chain.Next(null, chain.Spec.SlotsPerEpoch + slotInEpoch, full: false, 0xa1);
            spec = chain.Spec;
            anchor = first.Forked;
            child = chain.Next(first, first.Signed.Message!.Slot + 2, full: false, 0xa2).Forked;
            stateSsz = BeaconStateGloas.Encode(first.PostState);
            pubkeys.Build(first.PostState.Validators!);
        }
        else
        {
            UnsignedChain chain = UnsignedChain.Create();
            UnsignedChain.ChainBlock first = chain.Extend(chain.AnchorRoot, chain.Spec.SlotsPerEpoch + slotInEpoch, 0xa1);
            spec = chain.Spec;
            anchor = new ForkedSignedBeaconBlock.OfFulu(first.Block);
            child = new ForkedSignedBeaconBlock.OfFulu(chain.Extend(first.Root, first.Block.Message!.Slot + 2, 0xa2).Block);
            stateSsz = BeaconStateFulu.Encode(first.PostState);
            pubkeys.Build(first.PostState.Validators!);
        }

        using MemColumnsDb<BeaconChainDbColumns> db = new();
        BeaconChainStore store = new(db, spec);
        Hash256 anchorRoot = anchor.ComputeMessageRoot();
        store.PutForkedBlock(anchorRoot, anchor);
        store.PutState(anchorRoot, stateSsz);
        store.SetAnchor(anchorRoot, anchor.Slot);
        BeaconChainStore resumedStore = new(db, spec);
        Assert.That(resumedStore.TryGetAnchor(out Hash256? resumedRoot, out ulong resumedSlot), Is.True);
        Assert.That(resumedStore.TryGetForkedBlock(resumedRoot!, out ForkedSignedBeaconBlock? resumedBlock), Is.True);
        Assert.That(resumedStore.TryGetState(resumedRoot!, out byte[]? resumedSsz), Is.True);
        ForkedBeaconState resumedState = BeaconStateCodec.DecodeForked(resumedSsz!, spec);
        SlotClock clock = new(spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(spec.GenesisTime + child.Slot * spec.SecondsPerSlot)).UtcDateTime));
        BlockImporter importer = new(spec, resumedStore, pubkeys, new SignedGloasChain.EnvelopeEngine(), new BeaconChainConfig(),
            LimboLogs.Instance, ReplayedBlockAvailability.Instance, static (_, _) => true, clock, resumedState, resumedBlock!, resumedRoot!);
        Hash256 childRoot = child.ComputeMessageRoot();

        importer.OnSlotTick(clock.CurrentSlot);
        BlockImportResult result = importer.Import(child, childRoot, verifySignatures: gloas);
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        HeadView head = importer.ComputeHead();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumedSlot, Is.EqualTo(anchor.Slot));
            Assert.That(resumedRoot, Is.EqualTo(anchorRoot));
            Assert.That(resumedBlock!.ComputeMessageRoot(), Is.EqualTo(anchorRoot));
            Assert.That(head.HeadRoot, Is.EqualTo(childRoot));
            Assert.That(head.HeadSlot, Is.EqualTo(child.Slot));
            Assert.That(importer.IsKnown(childRoot), Is.True);
            Assert.That(((IBlockImporter)importer).RejectGossip, Is.False);
        }

        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        ProtoArrayForkChoice forkChoice = (ProtoArrayForkChoice)typeof(ForkChoiceRunner).GetField("_protoArray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;
        CheckpointRef bootstrap = runner.FinalizedCheckpoint;
        Hash256 unknown = Keccak.Compute("unknown bootstrap descendant");
        Func<Hash256, ulong, Hash256> checkpointBlock = typeof(ForkChoiceRunner).GetMethod("GetCheckpointBlock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<Hash256, ulong, Hash256>>(runner);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkChoice.GetAncestor(anchorRoot, bootstrap.Epoch * spec.SlotsPerEpoch), Is.EqualTo(slotInEpoch == 0 ? anchorRoot : null));
            Assert.That(checkpointBlock(childRoot, bootstrap.Epoch), Is.EqualTo(anchorRoot));
            Assert.That(() => checkpointBlock(unknown, bootstrap.Epoch), Throws.TypeOf<ForkChoiceException>());
            Assert.That(() => checkpointBlock(childRoot, bootstrap.Epoch - 1), Throws.TypeOf<ForkChoiceException>());
            Assert.That(runner.IsBootstrapCheckpointDescendant(childRoot, bootstrap), Is.EqualTo(slotInEpoch != 0));
            Assert.That(runner.IsBootstrapCheckpointDescendant(unknown, bootstrap), Is.False);
            Assert.That(runner.IsBootstrapCheckpointDescendant(childRoot, new CheckpointRef(bootstrap.Epoch, childRoot)), Is.False);
            Assert.That(runner.IsBootstrapCheckpointDescendant(childRoot, new CheckpointRef(bootstrap.Epoch, unknown)), Is.False);
        }

        ForkChoiceStore forkChoiceStore = (ForkChoiceStore)typeof(ForkChoiceRunner).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;
        CheckpointRef later = new(bootstrap.Epoch + 1, childRoot);
        forkChoiceStore.UpdateCheckpoints(later, later);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(runner.FinalizedCheckpoint, Is.EqualTo(later));
        Assert.That(runner.IsBootstrapCheckpointDescendant(childRoot, bootstrap), Is.False);
        Assert.That(runner.IsBootstrapCheckpointDescendant(childRoot, later), Is.False);
        if (slotInEpoch == 0)
            Assert.That(checkpointBlock(childRoot, bootstrap.Epoch), Is.EqualTo(anchorRoot));
        else
            Assert.That(() => checkpointBlock(childRoot, bootstrap.Epoch), Throws.TypeOf<ForkChoiceException>());
    }

    [Test]
    public void Finalization_forgets_failed_blocks_at_or_below_the_finalized_slot()
    {
        SignedGloasChain chain = new();
        FailedBlockRoots failed = new();
        BlockImporter importer = chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), failedBlocks: failed);
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(1);
        Hash256 atFinalized = Keccak.Compute("at the finalized slot");
        Hash256 afterFinalized = Keccak.Compute("after the finalized slot");
        failed.Add(atFinalized, finalizedSlot);
        failed.Add(afterFinalized, finalizedSlot + 1);

        importer.OnFinalized(new CheckpointRef(1, chain.AnchorRoot));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(failed.Contains(atFinalized), Is.False, "a finalized slot is no longer kept");
        Assert.That(failed.Contains(afterFinalized), Is.True);
    }

    [Test]
    public void Gossip_aggregate_refused_by_fork_choice_is_counted([Values] bool gloasContainer)
    {
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        importer.OnSlotTick(2);
        long refusedBefore = RefusedByForkChoice("gossip_aggregate");
        Attestation vote = chain.Vote(1, UnknownBlockRoot);

        if (gloasContainer)
        {
            AttestationGloas gloasVote = new() { AggregationBits = vote.AggregationBits, Data = vote.Data, Signature = vote.Signature, CommitteeBits = vote.CommitteeBits };
            importer.OnGossipAggregate(new SignedAggregateAndProofGloas { Message = new AggregateAndProofGloas { AggregatorIndex = 0, Aggregate = gloasVote } });
        }
        else
        {
            importer.OnGossipAggregate(new SignedAggregateAndProof { Message = new AggregateAndProof { AggregatorIndex = 0, Aggregate = vote } });
        }

        Assert.That(RefusedByForkChoice("gossip_aggregate") - refusedBefore, Is.EqualTo(1), "the aggregate reached fork choice, which refused its unknown head block");
    }

    [Test]
    public async Task Gossip_block_execution_verdict_does_not_charge_the_delivering_peer([Values(ExecutionStatus.Invalid, ExecutionStatus.Optimistic)] ExecutionStatus status)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), engine: new ScriptedPayloadEngine(status));
        await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, new ForkedSignedBeaconBlock.OfFulu(chain.Block),
            Snappy.CompressToArray(SignedBeaconBlock.Encode(chain.Block)), status == ExecutionStatus.Invalid ? MessageValidity.Ignored : MessageValidity.Accepted);
    }

    [Test]
    public async Task Gossip_block_with_unavailable_public_keys_does_not_charge_the_delivering_peer()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        FailedBlockRoots failed = new();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), failedBlocks: failed);
        chain.Pubkeys.Build([]);
        await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, new ForkedSignedBeaconBlock.OfFulu(chain.Block),
            Snappy.CompressToArray(SignedBeaconBlock.Encode(chain.Block)), MessageValidity.Ignored);
        Assert.That(failed.Contains(chain.BlockRoot), Is.False, "a local cache failure must not convict later gossip naming this block");
    }

    [Test]
    public async Task Gossip_fulu_block_proposer_faults_charge_the_delivering_peer([Values] bool invalidSignature, [Values] bool future)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), importClock: chain.ClockAtSlot(future ? 0UL : chain.Block.Message!.Slot));
        if (invalidSignature) chain.Block.Signature = default;
        else chain.Block.Message!.ProposerIndex = ulong.MaxValue;
        await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, new ForkedSignedBeaconBlock.OfFulu(chain.Block),
            Snappy.CompressToArray(SignedBeaconBlock.Encode(chain.Block)), future ? MessageValidity.Ignored : MessageValidity.Rejected);
    }

    public enum AggregatorForgery
    {
        None,
        NotSelected,
        SelectionProofByAnotherValidator,
        AggregatorSignatureByAnotherValidator,
        AggregatorOutsideTheCommittee,
        AggregateSignatureByAnotherValidator,
        CommitteeOutOfRange,
        WrongAggregationLength,
        WrongTarget,
        UnknownTarget,
        WrongTargetEpoch,
        MissingPublicKeys,
        UnknownBlock,
    }

    [Test]
    public async Task Gossip_aggregate_committee_range_precedes_timing_ignores(
        [Values] bool gloas, [Values] bool knownBlock, [Values] bool outOfRange, [Values(2UL, 64UL)] ulong slot)
    {
        UnsignedChain chain = UnsignedChain.Create();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        store.PutBlock(chain.AnchorRoot, chain.Anchor.AnchorBlock);
        SlotClock clock = chain.Anchor.ClockAtSlot(0);
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), importClock: clock, store: store);
        GossipRouter router = new(chain.Spec, clock, LimboLogs.Instance, store);
        Attestation vote = new()
        {
            Data = new AttestationData
            {
                Slot = slot,
                BeaconBlockRoot = knownBlock ? chain.AnchorRoot : UnknownBlockRoot,
                Source = new Checkpoint { Epoch = 0, Root = chain.AnchorRoot },
                Target = new Checkpoint { Epoch = chain.Spec.GetEpoch(slot), Root = chain.AnchorRoot },
            },
            AggregationBits = new BitArray(1, true),
            CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [outOfRange ? 1 : 0] = true },
        };
        (Func<byte[]> Encode, Func<GossipVerdict, BeaconSyncOrchestrator.WorkItem> Work, Action<ForkChoiceRunner> Apply) aggregate = AggregateGossip(new AggregateAndProof { Aggregate = vote }, gloas);

        await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, 0, GossipTopics.BeaconAggregateAndProof, aggregate.Encode(), aggregate.Work,
            knownBlock && outOfRange ? MessageValidity.Rejected : MessageValidity.Ignored, router, gloas);
    }

    // p2p-interface.md beacon_aggregate_and_proof: the aggregator is a committee member whose selection proof and
    // signature are valid, so a valid aggregate re-wrapped by anyone else is refused before its votes apply.
    [Test]
    public async Task Gossip_aggregate_applies_only_once_its_aggregator_authenticates([Values] bool gloasContainer, [Values] AggregatorForgery forgery)
    {
        const ulong slot = 1;
        UnsignedChain chain = UnsignedChain.Create(forgery == AggregatorForgery.NotSelected ? ImportableBlobBlock.Create(blobCount: 0, validatorCount: 1024) : null);
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), importClock: chain.Anchor.ClockAtSlot(slot + 1));
        importer.OnSlotTick(slot + 1);
        BeaconStateFulu state = chain.Anchor.AnchorState;
        byte[] slotRoot = new byte[32];
        BitConverter.TryWriteBytes(slotRoot, slot);
        int[] committee = new EpochCache().GetCommitteeCache(state, 0).GetBeaconCommittee(slot, 0).ToArray();
        int member = forgery == AggregatorForgery.NotSelected
            ? committee.First(index => !BeaconStateAccessors.IsAggregator(committee.Length, SignAs(index, new Hash256(slotRoot), DomainType.SelectionProof)))
            : committee.Single();
        int outsider = (member + 1) % state.Validators!.Length;
        int aggregator = forgery == AggregatorForgery.AggregatorOutsideTheCommittee ? outsider : member;
        Attestation vote = chain.Vote(slot, chain.AnchorRoot);
        vote.AggregationBits!.SetAll(false);
        vote.AggregationBits[Array.IndexOf(committee, member)] = true;
        vote.Signature = SignAs(forgery == AggregatorForgery.AggregateSignatureByAnotherValidator ? outsider : member, SszRoots.HashTreeRoot(vote.Data!), DomainType.BeaconAttester);
        BlsSignature selectionProof = SignAs(forgery == AggregatorForgery.SelectionProofByAnotherValidator ? outsider : aggregator, new Hash256(slotRoot), DomainType.SelectionProof);
        int signer = forgery == AggregatorForgery.AggregatorSignatureByAnotherValidator ? outsider : aggregator;

        if (forgery == AggregatorForgery.CommitteeOutOfRange)
        {
            vote.CommitteeBits!.SetAll(false);
            vote.CommitteeBits[1] = true;
        }
        if (forgery == AggregatorForgery.WrongAggregationLength) vote.AggregationBits!.Length++;
        if (forgery == AggregatorForgery.WrongTarget)
        {
            UnsignedChain.ChainBlock head = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xA1);
            Assert.That(importer.Import(head.Block, head.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
            vote.Data!.BeaconBlockRoot = head.Root;
            vote.Data.Target!.Root = head.Root;
            vote.Signature = SignAs(member, SszRoots.HashTreeRoot(vote.Data), DomainType.BeaconAttester);
        }
        if (forgery == AggregatorForgery.UnknownBlock) vote.Data!.BeaconBlockRoot = UnknownBlockRoot;
        if (forgery == AggregatorForgery.UnknownTarget) vote.Data!.Target!.Root = UnknownBlockRoot;
        if (forgery == AggregatorForgery.WrongTargetEpoch) vote.Data!.Target!.Epoch++;
        MessageValidity expected = forgery == AggregatorForgery.None ? MessageValidity.Accepted
            : forgery is AggregatorForgery.MissingPublicKeys or AggregatorForgery.UnknownBlock ? MessageValidity.Ignored : MessageValidity.Rejected;
        (Func<byte[]> Encode, Func<GossipVerdict, BeaconSyncOrchestrator.WorkItem> Work, Action<ForkChoiceRunner> Apply) aggregate = AggregateGossip(new AggregateAndProof { AggregatorIndex = (ulong)aggregator, Aggregate = vote, SelectionProof = selectionProof },
            gloasContainer, root => SignAs(signer, root, DomainType.AggregateAndProof));
        byte[] payload = aggregate.Encode();
        if (forgery == AggregatorForgery.MissingPublicKeys) chain.Anchor.Pubkeys.Build([]);
        await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, slot + 1, GossipTopics.BeaconAggregateAndProof, payload, aggregate.Work, expected);

        BlsSignature SignAs(int validator, Hash256 root, ReadOnlySpan<byte> domainType) =>
            ImportableBlobBlock.Sign(ImportableBlobBlock.DeriveKey(validator), root, state.GetDomain(domainType, 0));
    }

    [Test]
    public async Task Gossip_aggregate_valid_for_head_but_not_target_does_not_charge_the_delivering_peer([Values] bool gloasContainer)
    {
        const ulong epoch = 2;
        const ulong currentSlot = 3 * Presets.SlotsPerEpoch - 1;
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), importClock: chain.Anchor.ClockAtSlot(currentSlot));
        importer.OnSlotTick(currentSlot);
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, 30, payloadHashByte: 0xA0);
        UnsignedChain.ChainBlock a = chain.Extend(parent.Root, 31, payloadHashByte: 0xA1);
        UnsignedChain.ChainBlock b = chain.Extend(parent.Root, 32, payloadHashByte: 0xB1);
        foreach (UnsignedChain.ChainBlock block in new[] { parent, a, b })
            Assert.That(importer.Import(block.Block, block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        Hash256 head = runner.GetHead();
        Hash256 target = head == a.Root ? b.Root : a.Root;
        BeaconStateFulu headState = ((ForkedBeaconState.OfFulu)runner.GetCheckpointState(new CheckpointRef(epoch, head))).State;
        BeaconStateFulu targetState = ((ForkedBeaconState.OfFulu)runner.GetCheckpointState(new CheckpointRef(epoch, target))).State;
        CommitteeCache headCommittees = new EpochCache().GetCommitteeCache(headState, epoch);
        CommitteeCache targetCommittees = new EpochCache().GetCommitteeCache(targetState, epoch);
        ulong slot = Enumerable.Range((int)(epoch * Presets.SlotsPerEpoch), (int)Presets.SlotsPerEpoch - 1)
            .Select(static value => (ulong)value).First(value => headCommittees.GetBeaconCommittee(value, 0).Length == 1
                && targetCommittees.GetBeaconCommittee(value, 0).Length == 1
                && headCommittees.GetBeaconCommittee(value, 0)[0] != targetCommittees.GetBeaconCommittee(value, 0)[0]);
        ulong member = (ulong)headCommittees.GetBeaconCommittee(slot, 0)[0];
        AttestationData data = new() { Slot = slot, BeaconBlockRoot = target, Source = headState.CurrentJustifiedCheckpoint, Target = new Checkpoint { Epoch = epoch, Root = target } };
        byte[] slotRoot = new byte[32];
        BitConverter.TryWriteBytes(slotRoot, slot);
        AggregateAndProof message = new()
        {
            AggregatorIndex = member,
            SelectionProof = Sign(new Hash256(slotRoot), DomainType.SelectionProof),
            Aggregate = new Attestation
            {
                Data = data,
                AggregationBits = new BitArray(1, true),
                CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
                Signature = Sign(SszRoots.HashTreeRoot(data), DomainType.BeaconAttester),
            },
        };
        (Func<byte[]> Encode, Func<GossipVerdict, BeaconSyncOrchestrator.WorkItem> Work, Action<ForkChoiceRunner> Apply) aggregate = AggregateGossip(message, gloasContainer, root => Sign(root, DomainType.AggregateAndProof));
        Assert.That(() => aggregate.Apply(runner),
            Throws.TypeOf<ForkChoiceException>().With.Message.EqualTo("Attestation indices or aggregate signature are invalid"), "only target-state validation refuses this head-authenticated vote");
        await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, currentSlot, GossipTopics.BeaconAggregateAndProof,
            aggregate.Encode(), aggregate.Work, MessageValidity.Ignored);

        BlsSignature Sign(Hash256 root, ReadOnlySpan<byte> domain) => ImportableBlobBlock.SignAs(member, root, headState.GetDomain(domain, epoch));
    }

    private static (Func<byte[]> Encode, Func<GossipVerdict, BeaconSyncOrchestrator.WorkItem> Work, Action<ForkChoiceRunner> Apply) AggregateGossip(
        AggregateAndProof message, bool gloas, Func<Hash256, BlsSignature>? sign = null)
    {
        if (gloas)
        {
            Attestation vote = message.Aggregate!;
            AggregateAndProofGloas converted = new()
            {
                AggregatorIndex = message.AggregatorIndex,
                SelectionProof = message.SelectionProof,
                Aggregate = new AttestationGloas { Data = vote.Data, AggregationBits = vote.AggregationBits, CommitteeBits = vote.CommitteeBits, Signature = vote.Signature },
            };
            SignedAggregateAndProofGloas signed = new() { Message = converted, Signature = sign?.Invoke(SszRoots.HashTreeRoot(converted)) ?? default };
            return (() => Snappy.CompressToArray(SignedAggregateAndProofGloas.Encode(signed)),
                verdict => new BeaconSyncOrchestrator.GossipGloasAggregateItem(signed, verdict), runner => runner.OnAggregateAndProof(signed));
        }
        else
        {
            SignedAggregateAndProof signed = new() { Message = message, Signature = sign?.Invoke(SszRoots.HashTreeRoot(message)) ?? default };
            return (() => Snappy.CompressToArray(SignedAggregateAndProof.Encode(signed)),
                verdict => new BeaconSyncOrchestrator.GossipAggregateItem(signed, verdict), runner => runner.OnAggregateAndProof(signed));
        }
    }

    // validator.md is_aggregator: the little-endian first 8 bytes of sha256(proof) modulo max(1, committee size // 16) is zero.
    [TestCase(1, 31, true)]
    [TestCase(1, 32, false)]
    [TestCase(0, 32, true)]
    [TestCase(1, 47, false)]
    [TestCase(2, 128, true)]
    [TestCase(0, 128, false)]
    public void Selection_proof_selects_an_aggregator_by_its_hash(byte proofFill, int committeeSize, bool selected)
    {
        byte[] proof = Enumerable.Repeat(proofFill, BlsSignature.Length).ToArray();

        Assert.That(BeaconStateAccessors.IsAggregator(committeeSize, new BlsSignature(proof)), Is.EqualTo(selected));
    }

    [Test]
    public async Task Gossip_attester_slashing_requires_a_slashable_validator([Values] bool gloasContainer, [Values] bool allSlashed, [Values] bool headRetained)
    {
        UnsignedChain chain = UnsignedChain.Create();
        AttesterSlashing slashing = SignedDoubleVote(chain);

        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        if (!headRetained)
        {
            // p2p-interface.md attester_slashing still needs the head state even when the justified state is retained.
            ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
            PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
            states.Retain(chain.AnchorRoot, chain.Anchor.AnchorState.Clone());
            UnsignedChain.ChainBlock head = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
            Assert.That(importer.Import(head.Block, head.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
            Assert.That(runner.GetHead(), Is.EqualTo(head.Root));
            states.SetLineage(UnknownBlockRoot, chain.Anchor.AnchorState);
            Assert.That(states.GetHeldBlockState(head.Root), Is.Null);
        }

        // p2p-interface.md attester_slashing reads the retained head state; set the flag after anchoring to preserve the fixture root.
        chain.Anchor.AnchorState.Validators![1].Slashed = allSlashed;
        await AssertSlashingVerdictAsync(importer, slashing, gloasContainer,
            !headRetained ? MessageValidity.Ignored : allSlashed ? MessageValidity.Rejected : MessageValidity.Accepted, computeHead: headRetained);
    }

    [Test]
    public void Gossip_slashing_validation_preserves_cached_head_and_scores([Values] bool gloasContainer, [Values] bool headComputed)
    {
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), importClock: chain.Anchor.ClockAtSlot(2));
        importer.OnSlotTick(2);
        UnsignedChain.ChainBlock block = chain.Extend(chain.AnchorRoot, 1, payloadHashByte: 0xA1);
        Assert.That(importer.Import(block.Block, block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        if (headComputed) runner.GetHead();
        runner.OnAttestation(chain.Vote(1, block.Root), isFromBlock: true, verifySignature: false);
        ForkChoiceSnapshot before = runner.Snapshot();
        AttesterSlashing slashing = chain.DoubleVote([1], 1, chain.AnchorRoot, UnknownBlockRoot);
        slashing.Attestation1!.Signature = default;

        bool? accepted = gloasContainer
            ? importer.OnGossipAttesterSlashing(ToGloas(slashing))
            : importer.OnGossipAttesterSlashing(slashing);

        ForkChoiceSnapshot after = runner.Snapshot();
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(accepted, Is.EqualTo(headComputed ? (bool?)false : null), "a forgery is rejected only when local validation data is held");
        Assert.That(after.Nodes, Is.EqualTo(before.Nodes), "a refused slashing must not apply pending vote scores");
        Assert.That(after.ProposerBoostRoot, Is.EqualTo(before.ProposerBoostRoot));
        if (!headComputed)
            Assert.That(() => runner.GetHeldHeadState(), Throws.TypeOf<ForkChoiceException>().With.Message.Contains("No cached head"), "validation must not populate the head cache");
    }

    [Test]
    public async Task Gossip_slashing_invalid_indices_or_signatures_charge_the_delivering_peer([Values] bool gloasContainer,
        [Values(1, 2)] int attestation, [Values] bool outOfRange)
    {
        UnsignedChain chain = UnsignedChain.Create();
        AttesterSlashing slashing = SignedDoubleVote(chain);
        IndexedAttestation invalid = attestation == 1 ? slashing.Attestation1! : slashing.Attestation2!;
        if (outOfRange) invalid.AttestingIndices = [1, ulong.MaxValue];
        else invalid.Signature = default;
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        await AssertSlashingVerdictAsync(importer, slashing, gloasContainer, MessageValidity.Rejected);
    }

    [Test]
    public async Task Gossip_slashing_valid_only_for_head_preserves_voting_weights_and_peer_score([Values] bool gloasContainer,
        [Values(1, 2, 3)] int attestationsWithNewValidator)
    {
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool(), importClock: chain.Anchor.ClockAtSlot(8));
        importer.OnSlotTick(8);
        UnsignedChain.Equivocation scenario = chain.BuildEquivocation();
        foreach (UnsignedChain.ChainBlock block in new[] { scenario.A, scenario.B, scenario.Voted })
            Assert.That(importer.Import(block.Block, block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));

        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        Assert.That(runner.GetHead(), Is.EqualTo(scenario.Voted.Root), "the existing validators' votes must determine the head");
        ForkChoiceSnapshot before = runner.Snapshot();
        BeaconStateFulu justifiedState = states.GetHeldBlockState(before.JustifiedCheckpoint.Root)!;
        BeaconStateFulu headState = states.GetHeldBlockState(scenario.Voted.Root)!.Clone();
        ulong newIndex = (ulong)justifiedState.Validators!.Length;
        Validator deposited = new()
        {
            Pubkey = new BlsPublicKey(new Bls.P1(ImportableBlobBlock.DeriveKey((int)newIndex)).Compress()),
            WithdrawalCredentials = Hash256.Zero,
            EffectiveBalance = justifiedState.Validators[0].EffectiveBalance,
            ActivationEligibilityEpoch = Presets.FarFutureEpoch,
            ActivationEpoch = Presets.FarFutureEpoch,
            ExitEpoch = Presets.FarFutureEpoch,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        };
        headState.Validators = [.. headState.Validators!, deposited];
        states.Retain(scenario.Voted.Root, headState);
        chain.Anchor.Pubkeys.Build(headState.Validators);
        ulong[] existing = [.. chain.Committee(1), .. chain.Committee(3)];
        Array.Sort(existing);
        AttesterSlashing slashing = chain.DoubleVote(existing, 1, scenario.A.Root, scenario.B.Root);
        IndexedAttestation[] votes = [slashing.Attestation1!, slashing.Attestation2!];
        for (int i = 0; i < votes.Length; i++)
        {
            IndexedAttestation vote = votes[i];
            bool includesNewValidator = (attestationsWithNewValidator & (1 << i)) != 0;
            if (includesNewValidator) vote.AttestingIndices = [.. existing, newIndex];
            Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(vote.Data!), headState.GetDomain(DomainType.BeaconAttester, 0));
            BlsSigner.Signature signature = BlsSigner.Sign(ImportableBlobBlock.DeriveKey((int)existing[0]), signingRoot.Bytes);
            foreach (ulong index in vote.AttestingIndices!.Skip(1))
                signature.Aggregate(BlsSigner.Sign(ImportableBlobBlock.DeriveKey((int)index), signingRoot.Bytes));
            vote.Signature = new BlsSignature(signature.Bytes);
            Assert.That(BlockProcessing.IsValidIndexedAttestation(headState, vote, chain.Anchor.Pubkeys, verifySignature: true), Is.True,
                "both signatures must authenticate against the head with all keys cached");
            Assert.That(BlockProcessing.IsValidIndexedAttestation(justifiedState, vote, chain.Anchor.Pubkeys, verifySignature: true), Is.EqualTo(!includesNewValidator),
                "only the justified registry's missing validator may refuse the vote");
        }

        await AssertSlashingVerdictAsync(importer, slashing, gloasContainer, MessageValidity.Ignored);

        Hash256 headAfter = runner.GetHead();
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(headAfter, Is.EqualTo(scenario.Voted.Root), "a slashing refused by justified-state validation must not move the head");
        Assert.That(runner.Snapshot().Nodes.Select(node => (node.Root, node.Weight)),
            Is.EqualTo(before.Nodes.Select(node => (node.Root, node.Weight))), "neither indexed attestation may remove voting weight before both pass justified-state validation");
    }

    private static AttesterSlashing SignedDoubleVote(UnsignedChain chain)
    {
        AttesterSlashing slashing = chain.DoubleVote([1], 1, chain.AnchorRoot, UnknownBlockRoot);
        foreach (IndexedAttestation vote in new[] { slashing.Attestation1!, slashing.Attestation2! })
        {
            vote.Signature = ImportableBlobBlock.Sign(ImportableBlobBlock.DeriveKey(1), SszRoots.HashTreeRoot(vote.Data!),
                chain.Anchor.AnchorState.GetDomain(DomainType.BeaconAttester, vote.Data!.Target!.Epoch));
        }
        return slashing;
    }

    private static AttesterSlashingGloas ToGloas(AttesterSlashing slashing) => new()
    {
        Attestation1 = Convert(slashing.Attestation1!),
        Attestation2 = Convert(slashing.Attestation2!),
    };

    private static IndexedAttestationGloas Convert(IndexedAttestation vote) => new()
    {
        AttestingIndices = vote.AttestingIndices,
        Data = vote.Data,
        Signature = vote.Signature,
    };

    private static Task AssertSlashingVerdictAsync(BlockImporter importer, AttesterSlashing slashing, bool gloas, MessageValidity expected, bool computeHead = true)
    {
        if (gloas)
        {
            AttesterSlashingGloas converted = ToGloas(slashing);
            return BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, 2, GossipTopics.AttesterSlashing,
                Snappy.CompressToArray(AttesterSlashingGloas.Encode(converted)), verdict => new BeaconSyncOrchestrator.GossipGloasAttesterSlashingItem(converted, verdict), expected, computeHead: computeHead);
        }
        return BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, 2, GossipTopics.AttesterSlashing,
            Snappy.CompressToArray(AttesterSlashing.Encode(slashing)), verdict => new BeaconSyncOrchestrator.GossipAttesterSlashingItem(slashing, verdict), expected, computeHead: computeHead);
    }

    [Test]
    public void Gossip_attester_slashing_refused_by_fork_choice_is_counted([Values] bool gloasContainer)
    {
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        importer.ComputeHead();
        long refusedBefore = RefusedByForkChoice("gossip_attester_slashing");
        AttestationData data = chain.Vote(1, chain.AnchorRoot).Data!;

        AttesterSlashing slashing = new()
        {
            Attestation1 = new IndexedAttestation { AttestingIndices = [1], Data = data },
            Attestation2 = new IndexedAttestation { AttestingIndices = [1], Data = data },
        };
        bool? accepted = gloasContainer
            ? importer.OnGossipAttesterSlashing(ToGloas(slashing))
            : importer.OnGossipAttesterSlashing(slashing);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(RefusedByForkChoice("gossip_attester_slashing") - refusedBefore, Is.EqualTo(1), "the slashing reached fork choice, which refused it");
        Assert.That(accepted, Is.False, "a refused slashing must not mark its indices seen");
    }

    internal static (BeaconStateFulu State, SignedBeaconBlock Block, Hash256 Root) AnchorWithQueuedValidator(BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock)
    {
        BeaconStateFulu state = anchorState.Clone();
        state.PendingDeposits = [GloasTestFixtures.NewValidatorDeposit(keyIndex: 300, 32 * GloasTestFixtures.Gwei, slot: 0)];
        BeaconBlock anchor = anchorBlock.Message!;
        BeaconBlock message = new() { Slot = anchor.Slot, ProposerIndex = anchor.ProposerIndex, ParentRoot = anchor.ParentRoot, StateRoot = SszRoots.HashTreeRoot(state), Body = anchor.Body };
        return (state, new SignedBeaconBlock { Message = message, Signature = anchorBlock.Signature }, SszRoots.HashTreeRoot(message));
    }

    private static (SignedBeaconBlock Block, Hash256 Root, BeaconStateFulu PostState) UnsignedChild(BeaconStateFulu parentState, Hash256 parentRoot, ulong slot, AttesterSlashing[] slashings)
    {
        BlsSignature unsigned = new(SignatureSets.G2PointAtInfinity);
        BeaconBlock block = TestChain.CreateBlock(slot, parentRoot).Message!;
        block.ProposerIndex = 0;
        block.Body!.RandaoReveal = unsigned;
        block.Body.AttesterSlashings = slashings;
        Nethermind.BeaconChain.Types.ExecutionPayload payload = block.Body.ExecutionPayload!;
        payload.ParentHash = parentState.LatestExecutionPayloadHeader!.BlockHash;
        payload.PrevRandao = parentState.GetRandaoMix(parentState.GetCurrentEpoch());
        payload.Timestamp = parentState.GenesisTime + slot * Presets.SecondsPerSlot;
        payload.BlockNumber = parentState.LatestExecutionPayloadHeader.BlockNumber + 1;
        payload.BlockHash = new Hash256([.. Enumerable.Repeat((byte)slot, 32)]);
        SignedBeaconBlock signedBlock = new() { Message = block, Signature = unsigned };
        BeaconStateFulu postState = parentState.Clone();
        Nethermind.BeaconChain.StateTransition.StateTransition.Apply(postState, signedBlock, new EpochCache(), new PubkeyCache(), new GloasTestFixtures.AcceptingNotifier(), ImportableBlobBlock.FuluFromGenesis, validateResult: false, verifySignatures: false);
        block.StateRoot = SszRoots.HashTreeRoot(postState);
        return (signedBlock, SszRoots.HashTreeRoot(block), postState);
    }

    private static long RefusedByForkChoice(string operation) =>
        Metrics.BeaconChainForkChoiceRejections.GetValueOrDefault(new StringLabel(operation));

    private static BlockImporter CreateImporter(ImportableBlobBlock chain, NodeColumnCustody? custody, DataColumnSidecarPool pool, TestLogRecorder? warnings = null, IEngineDriver? engine = null, SlotClock? clock = null, ForkChoiceSnapshotHolder? forkChoiceSnapshots = null, SlotClock? importClock = null, ProposerLookaheadHolder? proposerLookaheads = null, FailedBlockRoots? failedBlocks = null, IDataAvailabilityRule? availability = null, BeaconChainStore? store = null, UnsignedChain.ChainBlock? anchor = null) =>
        new(
            chain.Spec,
            store ?? new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            chain.Pubkeys,
            engine ?? new ValidPayloadEngine(),
            new BeaconChainConfig(),
            warnings is null ? LimboLogs.Instance : new OneLoggerLogManager(new ILogger(warnings)),
            availability ?? new CustodySamplingAvailability(new Engine.TestEngineDriver.FixedCustodySource(custody), new DataColumnPoolSource(pool), clock ?? chain.ClockAtEpoch(0)),
            static (_, _) => false,
            importClock ?? new SlotClock(chain.Spec, Timestamper.Default),
            new ForkedBeaconState.OfFulu(anchor?.PostState ?? chain.AnchorState),
            new ForkedSignedBeaconBlock.OfFulu(anchor?.Block ?? chain.AnchorBlock),
            anchor?.Root ?? chain.AnchorRoot,
            forkChoiceSnapshots,
            proposerLookaheads,
            failedBlocks);

    private sealed class AvailableOnce : IDataAvailabilityRule
    {
        public int Calls { get; private set; }
        public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec) => ++Calls == 1;
    }

    private sealed class ValidPayloadEngine(Action? onPayload = null) : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public bool HasAnsweredNewPayload { get; private set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            HasAnsweredNewPayload = true;
            onPayload?.Invoke();
            return ExecutionStatus.Valid;
        }
    }

    private static ValidPayloadEngine UnavailableThenValidEngine()
    {
        bool failedOnce = false;
        return new ValidPayloadEngine(() =>
        {
            if (failedOnce) return;
            failedOnce = true;
            throw new EngineUnavailableException("newPayloadV4", "engine unavailable");
        });
    }

    private sealed class ScriptedPayloadEngine(params ExecutionStatus[] verdicts) : IEngineDriver
    {
        private int _call;

        public Hash256? LatestValidHash { get; init; }
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public bool HasAnsweredNewPayload { get; private set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            HasAnsweredNewPayload = true;
            return _call < verdicts.Length
                ? verdicts[_call++]
                : throw new InvalidOperationException($"The engine was called {_call + 1} times but only {verdicts.Length} verdicts were scripted");
        }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body, out Hash256? latestValidHash)
        {
            latestValidHash = LatestValidHash;
            return NotifyNewPayload(body);
        }
    }
}
