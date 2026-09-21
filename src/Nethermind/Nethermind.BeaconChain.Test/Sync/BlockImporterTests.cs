// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Network;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// The production data availability gate as <see cref="BlockImporter"/> applies it, driven end to
/// end through <see cref="BlockImporter.Import"/> with a genuinely valid signed blob block
/// (<see cref="ImportableBlobBlock"/>): availability is the only check left that can defer it.
/// At the base of this change the importer passed no columns at all to a rule that demands every
/// column, so every blob-carrying block was rejected; the positive case here is what proves the
/// gate now admits the blocks a base-custody node is actually able to verify.
/// </summary>
public class BlockImporterTests
{
    private static readonly Hash256 NodeId = new([.. Enumerable.Repeat((byte)0x42, 32)]);
    private static readonly Hash256 UnknownBlockRoot = new([.. Enumerable.Repeat((byte)0x99, 32)]);

    /// <summary>A base-custody node: four custody groups, eight sampled columns per slot on mainnet.</summary>
    private static NodeColumnCustody BaseCustody() => new(NodeId, Eip7594DasConstants.CustodyRequirement);

    [Test]
    public void Blob_block_with_every_sampled_column_held_and_verified_imports()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "eight verified columns out of 128 is exactly what a base-custody node can hold, and must suffice");
            Assert.That(importer.IsKnown(chain.BlockRoot), Is.True);
            Assert.That(custody.SampledColumns, Has.Count.EqualTo(8), "the fixture node is a base-custody node, not a supernode");
        });
    }

    [Test]
    public void Blob_block_missing_one_custody_column_is_deferred()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custody.CustodyColumns[0];
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        WarningCapture warnings = new();
        BlockImporter importer = CreateImporter(chain, custody, pool, warnings);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), "missing columns are retryable, not a permanent rejection");
            Assert.That(importer.IsKnown(chain.BlockRoot), Is.False, "a block whose data is unavailable must not enter fork choice");
            Assert.That(warnings.Warnings, Has.Some.Contains("blob data is not yet available"), "deferred for availability, not for some other reason");
        });
    }

    [Test]
    public void Blob_block_missing_a_sampled_but_not_custodied_column_is_deferred()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custody.SampledColumns.First(c => !custody.CustodyColumns.Contains(c));
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), "custody columns alone are not enough: the per-slot sample must succeed too");
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

    [Test]
    public void Held_column_that_fails_kzg_verification_does_not_count()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecar tampered = chain.Columns[(int)custody.CustodyColumns[0]];
        byte[] cell = tampered.Column![0].AsSpan().ToArray();
        cell[0] ^= 0xFF;
        tampered.Column[0] = SszBlobCell.FromSpan(cell);
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), "holding a column is not availability; the column must verify against the block's commitments");
    }

    [Test]
    public void Held_column_addressed_to_a_different_block_does_not_count()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        // Same index, same commitments, valid proofs: only the header names another block.
        chain.Columns[(int)custody.CustodyColumns[0]].SignedBlockHeader!.Message!.Slot = 999;
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable));
    }

    [Test]
    public void Block_without_blob_commitments_imports_with_no_columns_and_no_identity()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool());

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "a block with no blobs needs no columns");
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
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        // Resolves the identity and local custody exactly as Start does, without binding a socket.
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        NodeColumnCustody custody = new DiscoveryNodeCustodySource(discovery).Current!;
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporterFactory factory = new(chain.Spec, store, chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, pool, discovery, chain.ClockAtEpoch(0));
        IBlockImporter importer = factory.Create(chain.AnchorState, chain.AnchorBlock, chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(custody.CustodyGroupCount, Is.EqualTo(discovery.LocalCustody.CustodyGroupCount), "the importer demands the custody discovery advertises");
            Assert.That(custody.SampledColumns, Has.Count.EqualTo(8));
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the production wiring, end to end: eight held columns of this node's real identity admit the block");
        });
    }

    [Test]
    public void Factory_built_importer_applies_the_custody_rule_and_fails_closed_without_discovery()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => (ulong)c));
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, pool, clock: chain.ClockAtEpoch(0));
        IBlockImporter importer = factory.Create(chain.AnchorState, chain.AnchorBlock, chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable), "no discovery means no node id, so the custody rule has no columns to demand and must defer rather than pass");
    }

    [Test]
    public void Blob_block_below_the_availability_window_imports_with_no_columns_and_no_identity()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool(), clock: chain.ClockAtEpoch(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1));

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "outside the retention window nobody serves columns, so the gate must not wait for them");
            Assert.That(importer.IsKnown(chain.BlockRoot), Is.True);
        });
    }

    [Test]
    public void Factory_built_importer_measures_the_window_against_the_clock_it_is_given()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        SlotClock pastTheWindow = chain.ClockAtEpoch(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1);
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, new DataColumnSidecarPool(), clock: pastTheWindow);
        IBlockImporter importer = factory.Create(chain.AnchorState, chain.AnchorBlock, chain.AnchorRoot);

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

    /// <summary>
    /// An import must take the execution verdict from the <c>newPayload</c> call it made itself.
    /// Taking it from state shared with every other caller of the engine binds it to whichever
    /// call ran last, which can admit a block to fork choice as <see cref="ExecutionStatus.Valid"/>
    /// when the execution layer only accepted it optimistically. Fork choice cannot undo that:
    /// invalidating a node it already holds as valid throws, so the block is stuck in the tree.
    /// </summary>
    [TestCase(ExecutionStatus.Optimistic, false)]
    [TestCase(ExecutionStatus.Valid, true)]
    public void Import_takes_the_verdict_from_its_own_engine_call(ExecutionStatus verdict, bool sealedAgainstInvalidation)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        ScriptedPayloadEngine engine = new(ExecutionStatus.Valid, verdict);
        // Another caller's block was answered VALID on this same engine just before the import.
        engine.NotifyNewPayload(chain.Block.Message!.Body!);
        BlockImporter importer = CreateImporter(chain, custody, pool, engine: engine);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        IResolveConstraint invalidation = sealedAgainstInvalidation
            ? Throws.TypeOf<ProtoArrayException>()
            : Throws.Nothing;
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
            Assert.That(() => importer.OnInvalidExecutionPayload(chain.BlockRoot, null), invalidation,
                "a block admitted as valid is sealed against invalidation; one admitted optimistically must stay invalidatable");
        });
    }

    /// <summary>
    /// A block whose payload the execution layer never evaluated must stay importable. Two ways
    /// this used to go wrong: the failed call was reported as SYNCING and the block was imported
    /// optimistically anyway; and aborting the transition part-way leaves a trusted replay's
    /// in-place state with <c>LatestBlockHeader</c> already advanced, so the retry fails its own
    /// header check and the block is dropped as invalid for good.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void Block_whose_engine_call_fails_is_deferred_and_stays_importable(bool verifySignatures)
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool, engine: new UnavailableThenValidEngine());

        BlockImportResult deferred = importer.Import(chain.Block, chain.BlockRoot, verifySignatures);
        bool knownWhileDeferred = importer.IsKnown(chain.BlockRoot);
        BlockImportResult retried = importer.Import(chain.Block, chain.BlockRoot, verifySignatures);

        Assert.Multiple(() =>
        {
            Assert.That(deferred, Is.EqualTo(BlockImportResult.EngineUnavailable), "an unevaluated payload is not an invalid block");
            Assert.That(knownWhileDeferred, Is.False, "nothing may be recorded for a block the execution layer never saw");
            Assert.That(retried, Is.EqualTo(BlockImportResult.Imported), "the same block must import once the engine answers again");
        });
    }

    /// <summary>
    /// A block trailing its columns must never reach the engine at all: the availability check has
    /// to run before <c>newPayload</c>, not just produce the right label afterwards. A test that
    /// only asserted the returned result would still pass if a future edit moved the check back
    /// after the transition (gap 111's failure mode), so this asserts against a spy engine instead.
    /// </summary>
    [Test]
    public void Blob_block_missing_columns_never_calls_the_engine()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custody.CustodyColumns[0];
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        EngineCallSpy engine = new();
        BlockImporter importer = CreateImporter(chain, custody, pool, engine: engine);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable));
            Assert.That(engine.HasAnsweredNewPayload, Is.False, "the engine must not be consulted for a block that cannot be recorded anyway");
        });
    }

    /// <summary>The same block must import once its missing columns are later pooled, not stay dropped forever.</summary>
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

        Assert.Multiple(() =>
        {
            Assert.That(deferred, Is.EqualTo(BlockImportResult.DataUnavailable));
            Assert.That(knownWhileDeferred, Is.False, "nothing may be recorded while a column is still missing");
            Assert.That(retried, Is.EqualTo(BlockImportResult.Imported), "the same block must import once the missing column arrives");
        });
    }

    /// <summary>
    /// The importer's half of the body replay fork choice leaves to its callers: a block whose body
    /// slashes the two validators whose votes hold the head must move the head to the competing
    /// branch, which only happens if the accepted slashing is handed on to fork choice.
    /// </summary>
    [Test]
    public void Body_attester_slashing_moves_the_head_off_the_equivocators_branch()
    {
        UnsignedChain chain = UnsignedChain.Create();
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

        Assert.Multiple(() =>
        {
            Assert.That(imported, Is.All.EqualTo(BlockImportResult.Imported));
            Assert.That(headBeforeSlashing, Is.EqualTo(scenario.Voted.Root), "two votes on A's branch outweigh one on B");
            Assert.That(slashingImported, Is.EqualTo(BlockImportResult.Imported));
            Assert.That(importer.ComputeHead().HeadRoot, Is.EqualTo(scenario.B.Root), "the slashed validators' votes no longer count, so B's lone vote wins");
        });
    }

    /// <summary>
    /// A body attestation the transition accepts but fork choice refuses (its head is a block this
    /// node never saw) must neither sink the block nor vanish: the refusal is counted.
    /// </summary>
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

        Assert.Multiple(() =>
        {
            Assert.That(parent, Is.EqualTo(BlockImportResult.Imported));
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "a vote for a block we never saw is not a reason to drop a block the transition accepted");
            Assert.That(importer.IsKnown(strayVote.Root), Is.True);
            Assert.That(RefusedByForkChoice("body_attestation") - refusedBefore, Is.EqualTo(1), "a tolerated refusal must still be observable");
        });
    }

    [Test]
    public void Gossip_aggregate_refused_by_fork_choice_is_counted()
    {
        UnsignedChain chain = UnsignedChain.Create();
        BlockImporter importer = CreateImporter(chain.Anchor, custody: null, new DataColumnSidecarPool());
        importer.OnSlotTick(2);
        long refusedBefore = RefusedByForkChoice("gossip_aggregate");

        importer.OnGossipAggregate(new SignedAggregateAndProof { Message = new AggregateAndProof { AggregatorIndex = 0, Aggregate = chain.Vote(1, UnknownBlockRoot) } });

        Assert.That(RefusedByForkChoice("gossip_aggregate") - refusedBefore, Is.EqualTo(1));
    }

    private static long RefusedByForkChoice(string operation) =>
        Metrics.BeaconChainForkChoiceRejections.GetValueOrDefault(new StringLabel(operation));

    private static BlockImporter CreateImporter(ImportableBlobBlock chain, NodeColumnCustody? custody, DataColumnSidecarPool pool, WarningCapture? warnings = null, IEngineDriver? engine = null, SlotClock? clock = null) =>
        new(
            chain.Spec,
            new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            chain.Pubkeys,
            engine ?? new ValidPayloadEngine(),
            new BeaconChainConfig(),
            warnings is null ? LimboLogs.Instance : new OneLoggerLogManager(new ILogger(warnings)),
            new CustodySamplingAvailability(new FixedCustodySource(custody), new DataColumnPoolSource(pool), clock ?? chain.ClockAtEpoch(0)),
            chain.AnchorState,
            chain.AnchorBlock,
            chain.AnchorRoot);

    private sealed class FixedCustodySource(NodeColumnCustody? custody) : INodeColumnCustodySource
    {
        public NodeColumnCustody? Current => custody;
    }

    private sealed class FixedIPResolver(IPAddress ip) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(ip, ip));
    }

    private sealed class ValidPayloadEngine : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }

        public bool HasAnsweredNewPayload { get; private set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            HasAnsweredNewPayload = true;
            return ExecutionStatus.Valid;
        }
    }

    /// <summary>
    /// Records whether <c>newPayload</c> was ever called, for the hoisted-availability-check tests.
    /// Kept private to this file rather than shared: a hand-written <see cref="IEngineDriver"/> used
    /// elsewhere to check envelope-support enforcement must stay the only such double, or the two
    /// would collide.
    /// </summary>
    private sealed class EngineCallSpy : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }

        public bool HasAnsweredNewPayload { get; private set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            HasAnsweredNewPayload = true;
            return ExecutionStatus.Valid;
        }
    }

    /// <summary>Fails the first <c>newPayload</c> call and answers VALID afterwards: a transient engine outage.</summary>
    private sealed class UnavailableThenValidEngine : IEngineDriver
    {
        private bool _failedOnce;

        public SignedBeaconBlock? CurrentBlock { get; set; }

        public bool HasAnsweredNewPayload { get; private set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            HasAnsweredNewPayload = true;
            if (_failedOnce)
            {
                return ExecutionStatus.Valid;
            }

            _failedOnce = true;
            throw new EngineUnavailableException("newPayloadV4", "engine unavailable");
        }
    }

    /// <summary>
    /// Answers each <c>newPayload</c> call with the next scripted verdict, so a test can put an
    /// unrelated answer on the engine before the one the import under test should record.
    /// </summary>
    private sealed class ScriptedPayloadEngine(params ExecutionStatus[] verdicts) : IEngineDriver
    {
        private int _call;

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
    }

    private sealed class WarningCapture : InterfaceLogger
    {
        public List<string> Warnings { get; } = [];

        public bool IsInfo => false;
        public bool IsWarn => true;
        public bool IsDebug => false;
        public bool IsTrace => false;
        public bool IsError => true;

        public void Info(string text) { }
        public void Warn(string text) => Warnings.Add(text);
        public void Debug(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) => Warnings.Add(text);
    }
}
