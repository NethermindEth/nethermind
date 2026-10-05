// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Libp2p.Protocols.Pubsub;
using NSubstitute;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    private const ulong EnvelopeBlockSlot = 150;

    public enum EnvelopeCache
    {
        Absent,
        MemoryOnly,
        Persistent,
    }

    [Test]
    public async Task Envelope_import_persists_once_before_verification_and_recovers_from_storage_failure(
        [Values] EnvelopeCache cache, [Values(ExecutionStatus.Valid, ExecutionStatus.Optimistic)] ExecutionStatus verdict)
    {
        SignedGloasChain chain = new();
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        IColumnsDb<BeaconChainDbColumns> failingDb = Substitute.For<IColumnsDb<BeaconChainDbColumns>>();
        failingDb.GetColumnDb(Arg.Any<BeaconChainDbColumns>()).Returns(call => db.GetColumnDb(call.Arg<BeaconChainDbColumns>()));
        bool failWrites = false;
        failingDb.StartWriteBatch().Returns(_ =>
        {
            IColumnsWriteBatch<BeaconChainDbColumns> batch = db.StartWriteBatch();
            if (!failWrites) return batch;
            IColumnsWriteBatch<BeaconChainDbColumns> failingBatch = Substitute.For<IColumnsWriteBatch<BeaconChainDbColumns>>();
            failingBatch.GetColumnBatch(Arg.Any<BeaconChainDbColumns>()).Returns(call => batch.GetColumnBatch(call.Arg<BeaconChainDbColumns>()));
            failingBatch.When(b => b.Dispose()).Do(_ =>
            {
                batch.Clear();
                batch.Dispose();
                throw new InvalidOperationException("storage unavailable");
            });
            return failingBatch;
        });
        BeaconChainStore store = new(failingDb, chain.Spec);
        ExecutionPayloadEnvelopePool? pool = cache == EnvelopeCache.Absent ? null
            : new(store: cache == EnvelopeCache.Persistent ? store : null);
        (BeaconSyncOrchestrator orchestrator, _, SignedGloasChain.EnvelopeEngine engine) =
            CreateGloasOrchestrator(chain, store, envelopePool: pool);
        engine.EnvelopeVerdict = verdict;
        SignedGloasChain.Block block = chain.Next(null, 32, full: false, 0xC1);
        Assert.That(await orchestrator.ImportBlockAsync(block.Forked, CancellationToken.None), Is.EqualTo(BlockImportResult.Imported));
        MemDb column = (MemDb)db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        failWrites = true;

        ExecutionPayloadEnvelopeImportResult? failed = await orchestrator.ImportEnvelopeAsync(block.Envelope, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Is.Null, "a local storage failure is caught at the import recovery boundary");
            Assert.That(column.GetAllKeys(), Is.Empty, "a failed import must not leave an envelope or an orphan VALID marker");
            if (pool is not null) Assert.That(pool.TryGet(block.Root, out _), Is.False, "failed persistence must not populate the cache");
        }

        failWrites = false;
        long writesBefore = column.WritesCount;
        Assert.That(await orchestrator.ImportEnvelopeAsync(block.Envelope, CancellationToken.None),
            Is.EqualTo(verdict == ExecutionStatus.Valid ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.Optimistic),
            "a failed write must leave the payload unverified so persistence is retried instead of returning AlreadyKnown");
        Assert.That(await orchestrator.ImportEnvelopeAsync(block.Envelope, CancellationToken.None), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.AlreadyKnown));
        BeaconChainStore reopened = new(db, chain.Spec);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(column.WritesCount - writesBefore, Is.EqualTo(verdict == ExecutionStatus.Valid ? 4 : 3),
            "one envelope, slot index and bounds write, plus a marker for VALID; caching and re-import write nothing");
        Assert.That(reopened.TryGetExecutionPayloadEnvelope(block.Root, out _), Is.True, "AlreadyKnown must still leave a durable envelope for API reads and restart");
        Assert.That(reopened.IsExecutionPayloadValid(block.Root), Is.EqualTo(verdict == ExecutionStatus.Valid));
        Assert.That(engine.EnvelopeCalls, Is.EqualTo(2), "the failed import retries verification once, while AlreadyKnown does not");
        if (pool is not null) Assert.That(pool.TryGet(block.Root, out _), Is.True);
    }

    [Test]
    public async Task A_blob_carrying_stored_parent_recovers_columns_and_imports_its_full_child([Values] bool anchor, [Values] bool delayedColumns)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        ulong wallSlot = ColumnSlot + 2;
        ForkedSignedBeaconBlock parent = GloasBlobBlock(ColumnSlot, AnchorRoot(), ColumnSlot);
        Hash256 root = parent.ComputeMessageRoot();
        ExecutionPayloadBid bid = ((ForkedSignedBeaconBlock.OfGloas)parent).Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        store.PutForkedBlock(root, parent);
        DataColumnSidecarPool sidecars = new();
        int columnRequests = 0;
        EnvelopeServingPeer peer = new("peer", wallSlot, byRoot: _ => [EnvelopeFor(root, ColumnSlot)],
            gloasColumnsByRoot: ids => delayedColumns && ++columnRequests == 1 ? []
                : [.. ids[0].Columns!.Select(c => DataColumnSidecarGloasTestFixture.BuildSidecar(c, ColumnSlot, root))]);
        Harness harness = CreateHarness(wallSlot: wallSlot, store: store, peers: [peer], sidecarPool: sidecars, discovery: discovery);
        if (anchor) harness.Orchestrator.Initialize(harness.Importer, parent, root);
        harness.Importer.Known.UnionWith([AnchorRoot(), root]);
        harness.Importer.UnverifiedPayloads.Add(root);
        harness.Importer.Head = CreateHead(root, ColumnSlot, finalizedEpoch: Spec.GetEpoch(ColumnSlot));
        GloasCustodySamplingAvailability availability = new(new DiscoveryNodeCustodySource(discovery), sidecars, RangeSyncTests.ClockAtGenesis(Spec), Spec);
        Func<SignedExecutionPayloadEnvelope, ExecutionPayloadEnvelopeImportResult> envelopeVerdict = _ => availability.IsDataAvailable(root, bid)
            ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        harness.Importer.EnvelopeVerdict = envelopeVerdict;
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(ColumnSlot + 1, root));

        Assert.That(await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None), Is.EqualTo(BlockImportResult.ParentPayloadUnverified));
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        if (delayedColumns) harness.Importer.EnvelopeVerdict = _ => ExecutionPayloadEnvelopeImportResult.EngineUnavailable;
        await harness.Orchestrator.ProcessSlotAsync(wallSlot, CancellationToken.None);
        harness.Timestamper.Set(SlotStart(wallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(wallSlot + 1, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        if (delayedColumns)
        {
            Assert.That(peer.ColumnRootRequests, Has.Count.EqualTo(2));
            harness.Importer.EnvelopeVerdict = envelopeVerdict;
        }
        await harness.Orchestrator.ProcessSlotAsync(wallSlot + 1, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(peer.ColumnRootRequests, Has.Count.EqualTo(delayedColumns ? 2 : 1));
        Assert.That(peer.ColumnRootRequests[0][0].BlockRoot, Is.EqualTo(root));
        Assert.That(availability.IsDataAvailable(root, bid), Is.True);
        Assert.That(harness.Importer.Known.Contains(child.ComputeMessageRoot()), Is.True);
        Assert.That(harness.EnvelopePool.TryGet(root, out _), Is.True);
    }

    [Test]
    public async Task Gossip_envelope_reaches_the_importer_through_the_worker()
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + (FirstGloasSlot + 1) * Sepolia.SecondsPerSlot).AddSeconds(6));
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, timestamper), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, FirstGloasSlot + 1);

        MessageValidity validity = router.HandleExecutionPayloadEnvelope(Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(envelope)));
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(validity, Is.EqualTo(MessageValidity.Ignored), "fixture: the envelope names a block the router does not hold, so it is consumed");
        Assert.That(harness.Importer.Envelopes, Is.EqualTo(new[] { TestItem.KeccakA }));
    }

    [Test]
    public async Task Envelope_before_its_block_imports_once_the_block_imports_and_is_not_redelivered()
    {
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EnvelopeVerdict = e => harness.Importer.Known.Contains(e.Message!.BeaconBlockRoot!) ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.UnknownBlock;
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        Hash256 root = block.ComputeMessageRoot();
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(root, EnvelopeBlockSlot);

        ExecutionPayloadEnvelopeImportResult? early = await harness.Orchestrator.ImportEnvelopeAsync(envelope, CancellationToken.None);
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, EnvelopeBlockSlot, builderIndex: 1), CancellationToken.None);
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(early, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
        Assert.That(harness.Importer.ImportOrder, Is.EqualTo(new[] { (true, root), (true, root), (false, root), (true, root) }), "both held, the first imported right after its block, then neither again");
        Assert.That(harness.EnvelopePool.TryGet(root, out _), Is.True);
        Assert.That(harness.Router.IsEnvelopeSeen(root, envelope.Message!.BuilderIndex), Is.True);
    }

    [TestCase(false, TestName = "Parked_full_child_triggers_one_envelope_by_root_request_and_imports_in_the_same_pass")]
    [TestCase(true, TestName = "Gloas_anchor_first_full_child_recovers_the_anchor_envelope_by_root")]
    public async Task Parked_full_child_recovers_its_parents_envelope_by_root(bool parentIsAnchor)
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        Hash256 parentRoot = parentIsAnchor ? anchorRoot : parent.ComputeMessageRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot + 1, parentRoot));
        Hash256 childRoot = child.ComputeMessageRoot();
        EnvelopeServingPeer peer = new("peer", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], EnvelopeBlockSlot)]);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.UnionWith([anchorRoot, parentRoot]);
        harness.Importer.UnverifiedPayloads.Add(parentRoot);

        await harness.Orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(peer.RootRequests, Is.EqualTo(new[] { new[] { parentRoot } }), "one request, for the parent only");
        Assert.That(harness.Importer.ImportOrder, Is.EqualTo(new[] { (false, childRoot), (true, parentRoot), (false, childRoot) }));
        Assert.That(harness.Importer.Known, Does.Contain(childRoot));
        Assert.That(harness.Orchestrator.SyncTip, Is.EqualTo((childRoot, child.Slot)));
        Assert.That(harness.EnvelopePool.TryGet(parentRoot, out _), Is.True);
    }

    [Test]
    public async Task Parent_envelope_is_requested_by_root_at_most_once_per_slot()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        int requestsInFirstSlot = peer.RootRequests.Count;
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(requestsInFirstSlot, Is.EqualTo(1), "the slot tick retries the parked child without asking again");
        Assert.That(peer.RootRequests, Has.Count.EqualTo(2), "the next slot asks again");
    }

    [Test]
    public async Task Parent_envelope_recovery_reaches_the_fourth_peer_after_empty_replies()
    {
        Hash256 root = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, root));
        EnvelopeServingPeer[] silent = [.. Enumerable.Range(0, 3).Select(i => new EnvelopeServingPeer($"silent-{i}", WallSlot))];
        EnvelopeServingPeer serving = new("serving", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], AnchorSlot)]);
        Harness harness = CreateHarness(peers: [.. silent, serving]);
        harness.Importer.Known.Add(root);
        harness.Importer.UnverifiedPayloads.Add(root);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);
        Assert.That(serving.RootRequests, Is.Empty);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(silent.Select(static p => p.RootRequests.Count), Is.All.EqualTo(1));
        Assert.That(silent.SelectMany(static p => p.Reports), Is.Empty);
        Assert.That(serving.RootRequests, Has.Count.EqualTo(1));
        Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
    }

    [Test]
    public async Task Envelope_by_root_for_another_block_penalizes_its_peer()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        EnvelopeServingPeer lying = new("lying", WallSlot, byRoot: _ => [EnvelopeFor(TestItem.KeccakF, EnvelopeBlockSlot)]);
        EnvelopeServingPeer honest = new("honest", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], EnvelopeBlockSlot)]);
        Harness harness = CreateHarness(peers: [lying, honest]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(lying.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
        Assert.That(honest.Reports, Is.Empty);
        Assert.That(harness.Importer.Envelopes, Is.EqualTo(new[] { anchorRoot }), "the envelope for another block is never imported");
        Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
    }

    private sealed record RangePeerCase(int[]? Envelopes = null, ulong Head = WallSlot, ulong Earliest = 0,
        int? Calls = 1, int Failures = 0, (ulong, ulong)[]? Requests = null, bool RepeatFirst = false);
    private sealed record RangeEnvelopeCase(string Name, RangePeerCase[] Peers, int Blocks = 2, ulong Step = 1,
        ulong FirstSlot = AnchorSlot + 1, int[]? EmptyAt = null, bool FilterHead = false, bool Retry = false,
        bool CheckOrder = true, bool AwaitPayloads = true, int[]? Recorded = null, int? TotalRequests = null, bool NoRootRequests = false,
        bool CheckTip = false, RangeEnvelopeFault? Fault = null);
    private static readonly RangeEnvelopeCase[] RangeEnvelopeScenarios =
    [
        new("Batch envelopes immediately after their blocks", [new(Requests: [(AnchorSlot + 1, RangeSync.DefaultBatchSize), (AnchorSlot + 1 + RangeSync.DefaultBatchSize, 20 - RangeSync.DefaultBatchSize)], Calls: 2)], Blocks: 20, NoRootRequests: true, CheckTip: true),
        new("128-slot envelope request", [new(Head: 228, Requests: [(101, 128)])], Step: 127, AwaitPayloads: false),
        new("129-slot run splits", [new(Head: 229, Requests: [(101, 1), (229, 1)], Calls: 2)], Step: 128, AwaitPayloads: false),
        new("201-slot run splits", [new(Head: 301, Requests: [(101, 1), (301, 1)], Calls: 2)], Step: 200, AwaitPayloads: false),
        new("First signed envelope copy wins", [new(RepeatFirst: true)], AwaitPayloads: false),
        new("Off-chain payload is omitted", [new()], EmptyAt: [1], AwaitPayloads: false, Recorded: [1]),
        new("Envelope names no block", [new([1], Failures: 1)], EmptyAt: [1], AwaitPayloads: false, Recorded: [], Fault: RangeEnvelopeFault.BlockOutsideTheRun),
        new("Envelope has wrong slot", [new([1], Failures: 1)], EmptyAt: [1], AwaitPayloads: false, Recorded: [], Fault: RangeEnvelopeFault.WrongSlot),
        new("Envelope has wrong builder", [new([1], Failures: 1)], EmptyAt: [1], AwaitPayloads: false, Recorded: [], Fault: RangeEnvelopeFault.WrongBuilderIndex),
        new("Envelope has wrong payload hash", [new([1], Failures: 1)], EmptyAt: [1], AwaitPayloads: false, Recorded: [], Fault: RangeEnvelopeFault.WrongBlockHash),
        new("Three requests bound five empty peers", [new([], Calls: null), new([], Calls: null), new([], Calls: null), new([], Calls: null), new([], Calls: null)], CheckOrder: false, AwaitPayloads: false, TotalRequests: 3),
        new("Fourth peer is reached next slot", [new([]), new([]), new([]), new()], Blocks: 3, Retry: true, CheckOrder: false),
        new("Empty reply falls through", [new([]), new()], Blocks: 3),
        new("Short reply falls through", [new([0]), new()], Blocks: 3),
        new("Skipped on-chain payload blames supplier", [new([1], Failures: 1), new()], Blocks: 3),
        new("Peer serving start is preferred", [new(Earliest: 101), new(Calls: 0)]),
        new("Peer starting late is skipped", [new(Earliest: 102, Calls: 0), new()]),
        new("Peer reaching first slot can serve run", [new(Head: 102), new(Calls: 0)], Blocks: 3, FilterHead: true),
        new("Partial availability beats no envelopes", [new(Head: 101, Calls: 0), new(Earliest: 104, Calls: 0), new(Earliest: 103, Requests: [(102, 2)])], FirstSlot: 102, FilterHead: true),
        new("Complete reply stops peer requests", [new(), new([], Calls: 0)]),
    ];
    private static IEnumerable<TestCaseData> RangeEnvelopeCases()
    {
        for (int i = 0; i < RangeEnvelopeScenarios.Length; i++)
            yield return new TestCaseData(i).SetName(RangeEnvelopeScenarios[i].Name);
    }
    [TestCaseSource(nameof(RangeEnvelopeCases))]
    public async Task Range_envelope_requests_preserve_order_availability_and_peer_blame(int index)
    {
        RangeEnvelopeCase test = RangeEnvelopeScenarios[index];
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), test.FirstSlot, test.Blocks, test.Step, test.EmptyAt);
        SignedExecutionPayloadEnvelope[] envelopes = EnvelopesOf(chain);
        if (test.Fault is { } fault)
        {
            SignedExecutionPayloadEnvelope bad = envelopes[1];
            switch (fault)
            {
                case RangeEnvelopeFault.BlockOutsideTheRun: bad.Message!.BeaconBlockRoot = TestItem.KeccakF; break;
                case RangeEnvelopeFault.WrongSlot: bad.Message!.Payload!.SlotNumber++; break;
                case RangeEnvelopeFault.WrongBuilderIndex: bad.Message!.BuilderIndex++; break;
                case RangeEnvelopeFault.WrongBlockHash: bad.Message!.Payload!.BlockHash = TestItem.KeccakF; break;
            }
        }
        EnvelopeServingPeer[] peers = [.. test.Peers.Select((reply, peerIndex) =>
        {
            SignedExecutionPayloadEnvelope[] served = reply.Envelopes is null ? envelopes : [.. reply.Envelopes.Select(i => envelopes[i])];
            if (reply.RepeatFirst) served = [served[0], EnvelopeFor(chain[0]), .. served.Skip(1)];
            return RangeServingPeer($"peer-{peerIndex}", reply.Head, chain, served, reply.Earliest, serveEveryEnvelope: test.Fault is not null);
        })];
        Harness harness = CreateHarness(wallSlot: Math.Max(WallSlot, chain[^1].Slot), peers: peers, filterPoolByHead: test.FilterHead);
        harness.Importer.Known.Add(AnchorRoot());
        if (test.AwaitPayloads) harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));
        List<SignedExecutionPayloadEnvelope> imported = [];
        harness.Importer.EnvelopeVerdict = envelope => { imported.Add(envelope); return ExecutionPayloadEnvelopeImportResult.Valid; };
        if (test.Retry)
        {
            await harness.Orchestrator.FeedRangeSyncRoundAsync(CancellationToken.None);
            Assert.That(peers[^1].RangeRequests, Is.Empty, "the first three peers consume this slot's request budget");
            harness.Timestamper.Set(SlotStart(WallSlot + 1));
        }
        await RunRangeRoundAsync(harness);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        for (int p = 0; p < peers.Length; p++)
        {
            RangePeerCase expected = test.Peers[p];
            if (expected.Calls is { } calls) Assert.That(peers[p].RangeRequests, Has.Count.EqualTo(calls), peers[p].Id);
            if (expected.Requests is { } requests) Assert.That(peers[p].RangeRequests, Is.EqualTo(requests), peers[p].Id);
            Assert.That(peers[p].Reports, Is.EqualTo(Enumerable.Repeat(PeerFailureReason.ProtocolViolation, expected.Failures)), peers[p].Id);
            if (test.NoRootRequests) Assert.That(peers[p].RootRequests, Is.Empty, "no child waited for its parent's envelope");
        }
        if (test.TotalRequests is { } total) Assert.That(peers.Sum(static p => p.RangeRequests.Count), Is.EqualTo(total));
        if (test.CheckTip) Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(chain[^1].Slot));
        if (test.CheckOrder)
        {
            int[] recorded = test.Recorded ?? [.. Enumerable.Range(0, chain.Count)];
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(chain.SelectMany((b, i) => recorded.Contains(i)
                ? new[] { (false, b.ComputeMessageRoot()), (true, b.ComputeMessageRoot()) } : [(false, b.ComputeMessageRoot())])));
        }
        if (test.Fault is not null) Assert.That(harness.Importer.Envelopes, Is.Empty, "an unmatched envelope is discarded before import");
        if (test.Retry) Assert.That(harness.Importer.ImportOrder, Does.Contain((true, chain[0].ComputeMessageRoot())));
        if (test.TotalRequests is not null) Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(chain.Select(static b => b.Slot)), "the run is written out");
        if (test.Peers.Any(static p => p.RepeatFirst))
        {
            Assert.That(imported, Has.Count.EqualTo(2));
            Assert.That(imported[0], Is.SameAs(envelopes[0]), "the first signed copy is retained");
        }
    }
    public enum RangeEnvelopeFault
    {
        BlockOutsideTheRun,
        WrongSlot,
        WrongBuilderIndex,
        WrongBlockHash,
    }
    [Test]
    public async Task Envelope_waiting_on_its_data_or_the_engine_is_retried_on_the_tick_and_dropped_past_finality(
        [Values(ExecutionPayloadEnvelopeImportResult.DataUnavailable, ExecutionPayloadEnvelopeImportResult.EngineUnavailable)] ExecutionPayloadEnvelopeImportResult waiting,
        [Values] bool finalizedPast)
    {
        Harness harness = CreateHarness();
        ulong finalizedEpoch = Spec.GetEpoch(EnvelopeBlockSlot) + 1;
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, finalizedEpoch * Spec.SlotsPerEpoch);
        harness.Importer.EnvelopeVerdict = _ => harness.Importer.Envelopes.Count == 1 ? waiting : ExecutionPayloadEnvelopeImportResult.Valid;
        if (finalizedPast)
        {
            harness.Importer.Head = CreateHead(TestItem.KeccakB, AnchorSlot, finalizedEpoch: finalizedEpoch);
        }

        await harness.Orchestrator.ImportEnvelopeAsync(envelope, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(finalizedPast ? 1 : 2), "retried once, until it imports, unless finality passed its slot");
        Assert.That(harness.EnvelopePool.TryGet(TestItem.KeccakA, out _), Is.EqualTo(!finalizedPast));
    }

    [Test]
    public async Task Envelope_waiting_on_its_data_expires_under_stalled_finality()
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.KeccakA, WallSlot), CancellationToken.None);

        int attemptsNextSlot = await TickAtAgeAsync(harness, 1, () => harness.Importer.Envelopes.Count);
        int attemptsWithinAge = await TickAtAgeAsync(harness, RetryAgeSlots, () => harness.Importer.Envelopes.Count);
        int attemptsPastAge = await TickAtAgeAsync(harness, RetryAgeSlots + 1, () => harness.Importer.Envelopes.Count);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(attemptsNextSlot, Is.EqualTo(2));
        Assert.That(attemptsWithinAge, Is.EqualTo(3), "a retry that still waits stays queued once, from the slot it was first queued at");
        Assert.That(attemptsPastAge, Is.EqualTo(3), "an envelope older than the retry age is dropped although finality never passed it");
    }

    [Test]
    public async Task Data_unavailable_block_expires_from_the_retry_set_under_stalled_finality()
    {
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot, anchorRoot));
        harness.Importer.Unavailable.Add(block.ComputeMessageRoot());
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);

        int attemptsWithinAge = await TickAtAgeAsync(harness, RetryAgeSlots, () => harness.Importer.Imports.Count);
        int attemptsPastAge = await TickAtAgeAsync(harness, RetryAgeSlots + 1, () => harness.Importer.Imports.Count);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(attemptsWithinAge, Is.EqualTo(2));
        Assert.That(attemptsPastAge, Is.EqualTo(2), "the stuck block is dropped although finality never passed it");
    }

    public static IEnumerable<ExecutionPayloadEnvelopeImportResult> EveryEnvelopeResultAndAnUnknownOne() =>
        [.. Enum.GetValues<ExecutionPayloadEnvelopeImportResult>(), (ExecutionPayloadEnvelopeImportResult)99];

    [Test]
    public async Task Envelope_is_marked_seen_and_pooled_only_after_its_payload_is_recorded([ValueSource(nameof(EveryEnvelopeResultAndAnUnknownOne))] ExecutionPayloadEnvelopeImportResult result)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = result;
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);

        await harness.Orchestrator.ImportEnvelopeAsync(envelope, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Router.IsEnvelopeSeen(TestItem.KeccakA, envelope.Message!.BuilderIndex),
            Is.EqualTo(result is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic or ExecutionPayloadEnvelopeImportResult.AlreadyKnown));
        Assert.That(harness.EnvelopePool.TryGet(TestItem.KeccakA, out SignedExecutionPayloadEnvelope? served) ? served : null,
            Is.EqualTo(result is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic ? envelope : null));
    }

    [Test]
    public async Task Invalid_envelope_penalizes_only_the_req_resp_peer_that_served_it([Values] bool fromPeer, [Values] bool afterDataRetry)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeVerdict = _ => afterDataRetry && harness.Importer.Envelopes.Count == 1
            ? ExecutionPayloadEnvelopeImportResult.DataUnavailable
            : ExecutionPayloadEnvelopeImportResult.Invalid;
        EnvelopeServingPeer peer = new("peer", WallSlot);
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);

        harness.Orchestrator.WorkWriter.TryWrite(fromPeer ? new BeaconSyncOrchestrator.FetchedEnvelopeItem(envelope, peer) : new BeaconSyncOrchestrator.GossipEnvelopeItem(envelope));
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);
        await TickAtAgeAsync(harness, 1, () => 0);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(afterDataRetry ? 2 : 1), "fixture: an envelope waiting on its data is retried on the tick");
        Assert.That(peer.Reports, Is.EqualTo(fromPeer ? new[] { PeerFailureReason.ProtocolViolation } : []));
    }

    [Test]
    public async Task Envelope_import_fault_is_dropped_without_stopping_the_worker([Values] bool withoutMessage)
    {
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EnvelopeVerdict = static _ => throw new InvalidOperationException("no execution verdict");
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(AnchorSlot + 1, anchorRoot));
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);
        ulong builderIndex = envelope.Message!.BuilderIndex;
        if (withoutMessage)
        {
            envelope.Message = null;
        }

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(envelope));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(block));
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Known, Does.Contain(block.ComputeMessageRoot()), "the next item is still processed");
        Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(withoutMessage ? 0 : 1), "an envelope without a message never reaches the importer");
        Assert.That(harness.Router.IsEnvelopeSeen(TestItem.KeccakA, builderIndex), Is.False);
        Assert.That(harness.EnvelopePool.TryGet(TestItem.KeccakA, out _), Is.False);
    }

    [Test]
    public async Task Orchestrator_from_the_module_pools_recorded_envelopes_where_by_root_serves_them()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        BeaconSyncOrchestrator orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        ExecutionPayloadEnvelopePool pool = container.Resolve<ExecutionPayloadEnvelopePool>();
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        orchestrator.Initialize(new ScriptedImporter { Head = CreateHead(anchorRoot, AnchorSlot, finalizedEpoch: 0) }, new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot);

        await orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot), CancellationToken.None);

        Assert.That(pool.TryGet(TestItem.KeccakA, out _), Is.True);
    }

    private sealed record ColumnRecoveryCase(string Name, int SilentPeers, int EmptyReplies, bool FloodCandidates,
        bool SameSlotTick, ulong Age, bool Finalized, int Requests, bool? InitiallyAvailable, bool? FinallyAvailable,
        bool SettleOnImport = true, bool SettleAfterSameSlot = true);
    private static readonly ColumnRecoveryCase[] ColumnRecoveryScenarios =
    [
        new("Forged candidates cannot suppress column recovery", 0, 0, true, true, 0, false, 1, true, null, SettleOnImport: false, SettleAfterSameSlot: false),
        new("One recovery per slot, then retry", 0, 1, false, true, 1, false, 2, null, true),
        new("Recovery rotates past three silent custodians", 3, 0, false, false, 1, false, 1, false, true),
        new("Finality ends column recovery", 0, int.MaxValue, false, true, 1, true, 1, null, null, SettleAfterSameSlot: false),
        new("Retry age ends column recovery", 0, int.MaxValue, false, true, RetryAgeSlots + 1, false, 1, null, null, SettleAfterSameSlot: false),
    ];
    private static IEnumerable<TestCaseData> ColumnRecoveryCases()
    {
        for (int i = 0; i < ColumnRecoveryScenarios.Length; i++)
            yield return new TestCaseData(i).SetName(ColumnRecoveryScenarios[i].Name);
    }
    [TestCaseSource(nameof(ColumnRecoveryCases))]
    public async Task Column_recovery_is_bounded_by_arrival_slot_peer_rotation_finality_and_age(int index)
    {
        ColumnRecoveryCase test = ColumnRecoveryScenarios[index];
        await using BeaconDiscovery discovery = CreateDiscovery();
        ForkedSignedBeaconBlock block = GloasBlobBlock(EnvelopeBlockSlot, AnchorRoot(), ColumnSlot);
        ExecutionPayloadBid bid = ((ForkedSignedBeaconBlock.OfGloas)block).Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        Hash256 root = block.ComputeMessageRoot();
        DataColumnSidecarPool sidecars = new();
        DiscoveryNodeCustodySource custody = new(discovery);
        if (test.FloodCandidates)
        {
            for (int candidate = 0; candidate < DataColumnSidecarPool.MaxPendingGloasCandidatesPerKey; candidate++)
            {
                foreach (ulong column in custody.Current!.SampledColumns)
                {
                    DataColumnSidecarGloas forged = DataColumnSidecarGloasTestFixture.BuildSidecar(column, ColumnSlot, root);
                    forged.KzgProofs = [forged.KzgProofs![1], forged.KzgProofs[0]];
                    sidecars.AddPendingGloas(forged, ColumnSlot);
                }
            }
        }
        EnvelopeServingPeer[] silent = [.. Enumerable.Range(0, test.SilentPeers).Select(i => new EnvelopeServingPeer($"silent-{i}", WallSlot))];
        int calls = 0;
        EnvelopeServingPeer serving = new("serving", WallSlot, gloasColumnsByRoot: ids => ++calls <= test.EmptyReplies
            ? [] : [.. ids[0].Columns!.Select(c => DataColumnSidecarGloasTestFixture.BuildSidecar(c, ColumnSlot, root))]);
        Harness harness = CreateHarness(peers: [.. silent, serving],
            sidecarPool: test.InitiallyAvailable is not null || test.FinallyAvailable is not null ? sidecars : null, discovery: discovery);
        harness.Importer.Known.Add(AnchorRoot());
        GloasCustodySamplingAvailability availability = new(custody, sidecars, RangeSyncTests.ClockAtGenesis(Spec), Spec);
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        if (test.SettleOnImport) await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        bool availableOnImport = test.InitiallyAvailable is not null && availability.IsDataAvailable(root, bid);
        int onImport = serving.ColumnRootRequests.Count;
        if (test.SameSlotTick)
        {
            await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
            if (test.SettleAfterSameSlot) await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
            Assert.That(serving.ColumnRootRequests.Count, Is.EqualTo(onImport), "at most once per block per slot");
        }
        if (test.Finalized) harness.Importer.Head = CreateHead(TestItem.KeccakB, AnchorSlot, finalizedEpoch: Spec.GetEpoch(ColumnSlot) + 1);
        if (test.Age > 0)
        {
            harness.Timestamper.Set(SlotStart(WallSlot + test.Age));
            await harness.Orchestrator.ProcessSlotAsync(WallSlot + test.Age, CancellationToken.None);
            await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        }
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(onImport, Is.EqualTo(test.SilentPeers == 0 ? 1 : 0), "recovery starts at import");
        Assert.That(serving.ColumnRootRequests, Has.Count.EqualTo(test.Requests));
        Assert.That(silent.Select(static p => p.ColumnRootRequests.Count), Is.All.EqualTo(1), "each silent custodian is asked once");
        if (test.InitiallyAvailable is { } initial) Assert.That(availableOnImport, Is.EqualTo(initial));
        if (test.FinallyAvailable is { } final) Assert.That(availability.IsDataAvailable(root, bid), Is.EqualTo(final));
        if (test.FloodCandidates) Assert.That(serving.ColumnRootRequests[0][0].BlockRoot, Is.EqualTo(root));
    }
    [Test]
    public async Task Range_feed_writes_out_buffered_gloas_blocks_before_a_later_pre_gloas_block()
    {
        List<ForkedSignedBeaconBlock.OfGloas> gloas = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 1);
        ForkedSignedBeaconBlock fulu = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(AnchorSlot + 2, gloas[0].ComputeMessageRoot()));
        EnvelopeServingPeer peer = new("peer", WallSlot, blocksByRange: (start, count) => [.. new[] { gloas[0], fulu }.Where(b => b.Slot >= start && b.Slot - start < count)]);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(AnchorRoot());

        await RunRangeRoundAsync(harness);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(new[] { AnchorSlot + 1, AnchorSlot + 2 }));
    }

    [TestCase(true, TestName = "Parked_full_child_takes_its_parents_envelope_from_those_held_before_asking_a_peer")]
    [TestCase(false, TestName = "Parent_envelope_waiting_on_its_data_is_not_requested_by_root")]
    public async Task Held_parent_envelope_is_used_before_asking_a_peer(bool parentBecomesKnown)
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);
        if (parentBecomesKnown)
        {
            harness.Importer.EnvelopeVerdict = _ => harness.Importer.Envelopes.Count == 1 ? ExecutionPayloadEnvelopeImportResult.UnknownBlock : ExecutionPayloadEnvelopeImportResult.Valid;
        }
        else
        {
            harness.Importer.EnvelopeResult = ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        }
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(anchorRoot, AnchorSlot), CancellationToken.None);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(peer.RootRequests, Is.Empty);
        if (parentBecomesKnown)
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
    }

    [Test]
    public async Task Child_of_a_parked_block_asks_for_no_envelope_of_its_own_parent()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot + 1, parent.ComputeMessageRoot()));
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);

        await harness.Orchestrator.ImportBlockAsync(parent, CancellationToken.None);
        BlockImportResult childResult = await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(childResult, Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture: the child is deferred behind its parked parent");
        Assert.That(peer.RootRequests, Is.EqualTo(new[] { new[] { anchorRoot } }));
    }

    [Test]
    public async Task Gossip_block_imports_after_its_fetched_parent_recovers_its_parents_envelope()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(NearWallSlot);
        ForkedSignedBeaconBlock full = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(NearWallSlot + 1, anchorRoot));
        Hash256 fullRoot = full.ComputeMessageRoot();
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(NearWallSlot + 2, fullRoot));
        ForkedSignedBeaconBlock gossip = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(NearWallSlot + 3, parent.ComputeMessageRoot()));
        EnvelopeServingPeer peer = new("peer", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], NearWallSlot + 1)], blocksByRoot: _ => [parent]);
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.UnionWith([anchorRoot, fullRoot]);
        harness.Importer.UnverifiedPayloads.Add(fullRoot);

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(gossip, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Known, Does.Contain(parent.ComputeMessageRoot()));
        Assert.That(harness.Importer.Known, Does.Contain(gossip.ComputeMessageRoot()));
    }

    public enum HeldEnvelopeFlood
    {
        OneBlockFloods,
        OtherBlocksFillFirst,
        OtherBlocksFillAfterTheFirst,
    }

    [TestCase(HeldEnvelopeFlood.OneBlockFloods, 4)]
    [TestCase(HeldEnvelopeFlood.OtherBlocksFillFirst, 0)]
    [TestCase(HeldEnvelopeFlood.OtherBlocksFillAfterTheFirst, 1)]
    public async Task Envelopes_held_for_a_block_not_imported_yet_are_bounded_first_come(HeldEnvelopeFlood flood, int expectedImports)
    {
        const int PerBlock = 4;
        const int Total = 128;
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EnvelopeVerdict = e => harness.Importer.Known.Contains(e.Message!.BeaconBlockRoot!) ? ExecutionPayloadEnvelopeImportResult.Invalid : ExecutionPayloadEnvelopeImportResult.UnknownBlock;
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        Hash256 root = block.ComputeMessageRoot();
        int held = 0;
        if (flood == HeldEnvelopeFlood.OtherBlocksFillAfterTheFirst)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, EnvelopeBlockSlot, builderIndex: 0), CancellationToken.None);
            held = 1;
        }

        if (flood != HeldEnvelopeFlood.OneBlockFloods)
        {
            for (int i = held; i < Total; i++)
            {
                await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.Keccaks[i], EnvelopeBlockSlot), CancellationToken.None);
            }
        }

        for (int i = held; i <= PerBlock; i++)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, EnvelopeBlockSlot, builderIndex: (ulong)i), CancellationToken.None);
        }

        int beforeBlock = harness.Importer.Envelopes.Count;
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);

        Assert.That(harness.Importer.Envelopes.Count - beforeBlock, Is.EqualTo(expectedImports));
    }

    [Test]
    public async Task Envelopes_held_for_blocks_that_never_import_give_up_their_places([Values] bool finalized)
    {
        const int Total = 128;
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EnvelopeVerdict = e => harness.Importer.Known.Contains(e.Message!.BeaconBlockRoot!) ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.UnknownBlock;
        for (int i = 0; i < Total; i++)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.Keccaks[i], EnvelopeBlockSlot), CancellationToken.None);
        }

        if (finalized)
        {
            harness.Importer.Head = CreateHead(TestItem.KeccakB, AnchorSlot, finalizedEpoch: Spec.GetEpoch(EnvelopeBlockSlot) + 1);
        }

        ulong age = finalized ? 1 : RetryAgeSlots + 1;
        await TickAtAgeAsync(harness, age, () => 0);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(WallSlot + age, anchorRoot));
        Hash256 root = block.ComputeMessageRoot();
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, block.Slot), CancellationToken.None);
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.ImportOrder.TakeLast(3), Is.EqualTo(new[] { (true, root), (false, root), (true, root) }), "held, then imported right after its block");
        Assert.That(harness.EnvelopePool.TryGet(root, out _), Is.True);
    }

    [Test]
    public async Task Column_recovery_keeps_the_newest_blocks_when_full()
    {
        const int Capacity = 32;
        await using BeaconDiscovery discovery = CreateDiscovery();
        Hash256 anchorRoot = AnchorRoot();
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer], discovery: discovery);
        harness.Importer.Known.Add(anchorRoot);
        List<Hash256> roots = [];
        for (int i = 0; i <= Capacity; i++)
        {
            ForkedSignedBeaconBlock block = GloasBlobBlock(EnvelopeBlockSlot + (ulong)i, anchorRoot, ColumnSlot + (ulong)i);
            roots.Add(block.ComputeMessageRoot());
            await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        }

        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        int requestsOnImport = peer.ColumnRootRequests.Count;
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);

        Assert.That(peer.ColumnRootRequests.Skip(requestsOnImport).Select(static r => r[0].BlockRoot), Is.EquivalentTo(roots.Skip(1)));
    }

    private static ulong ColumnSlot => Spec.FuluForkEpoch * Spec.SlotsPerEpoch;

    private static ForkedSignedBeaconBlock GloasBlobBlock(ulong slot, Hash256 parentRoot, ulong bidSlot)
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
        ExecutionPayloadBid bid = block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        bid.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        bid.Slot = bidSlot;
        return new ForkedSignedBeaconBlock.OfGloas(block);
    }

    private static BeaconDiscovery CreateDiscovery()
    {
        BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        return discovery;
    }

    private static ulong RetryAgeSlots => 2 * Spec.SlotsPerEpoch;

    private static Hash256 AnchorRoot() => TestChain.BuildLinkedChain(AnchorSlot).AnchorRoot;

    private static DateTime SlotStart(ulong slot) => DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot).AddSeconds(6);

    private static async Task<int> TickAtAgeAsync(Harness harness, ulong age, Func<int> attempts)
    {
        harness.Timestamper.Set(SlotStart(WallSlot + age));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + age, CancellationToken.None);
        return attempts();
    }

    private static SignedExecutionPayloadEnvelope EnvelopeFor(Hash256 blockRoot, ulong slot, ulong builderIndex = 0, Hash256? blockHash = null) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas { SlotNumber = slot, BlockHash = blockHash ?? Hash256.Zero },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = builderIndex,
            BeaconBlockRoot = blockRoot,
            ParentBeaconBlockRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };

    private static SignedExecutionPayloadEnvelope EnvelopeFor(ForkedSignedBeaconBlock.OfGloas block)
    {
        ExecutionPayloadBid bid = block.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        return EnvelopeFor(block.ComputeMessageRoot(), block.Slot, bid.BuilderIndex, bid.BlockHash);
    }

    private static SignedExecutionPayloadEnvelope[] EnvelopesOf(List<ForkedSignedBeaconBlock.OfGloas> chain) => [.. chain.Select(EnvelopeFor)];

    private static List<ForkedSignedBeaconBlock.OfGloas> BuildGloasRun(Hash256 anchorRoot, ulong firstSlot, int count, ulong slotStep = 1, int[]? emptyAt = null)
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = [];
        Hash256 parentRoot = anchorRoot;
        Hash256 parentPayloadHash = Keccak.Compute("anchor payload");
        Hash256 grandparentPayloadHash = Keccak.Compute("before the anchor payload");
        for (int i = 0; i < count; i++)
        {
            ulong slot = firstSlot + (ulong)i * slotStep;
            SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
            ExecutionPayloadBid bid = block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
            bid.BuilderIndex = 7;
            bid.BlockHash = Keccak.Compute($"payload {slot}");
            bid.ParentBlockHash = emptyAt?.Contains(i) == true ? grandparentPayloadHash : parentPayloadHash;
            chain.Add(new ForkedSignedBeaconBlock.OfGloas(block));
            parentRoot = chain[^1].ComputeMessageRoot();
            grandparentPayloadHash = bid.ParentBlockHash;
            parentPayloadHash = bid.BlockHash;
        }

        return chain;
    }

    private static EnvelopeServingPeer RangeServingPeer(
        string id, ulong headSlot, List<ForkedSignedBeaconBlock.OfGloas> chain, SignedExecutionPayloadEnvelope[] envelopes,
        ulong earliestAvailableSlot = 0, bool serveEveryEnvelope = false) => new(
            id,
            headSlot,
            byRange: (start, count) => [.. envelopes.Where(e => serveEveryEnvelope || (e.Message!.Payload!.SlotNumber >= start && e.Message.Payload.SlotNumber - start < count))],
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)],
            earliestAvailableSlot: earliestAvailableSlot);

    private static async Task RunRangeRoundAsync(Harness harness)
    {
        await harness.Orchestrator.FeedRangeSyncRoundAsync(CancellationToken.None);
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);
    }
}
