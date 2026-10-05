// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Api;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;

namespace Nethermind.BeaconChain.Test.Sync;

public class DeferredBlockColumnFetchTests
{
    private const int CustodiansPerImport = 3;

    [Test]
    [CancelAfter(30_000)]
    public async Task A_deferred_block_imports_in_the_same_slot_once_a_custodian_of_its_missing_columns_connects(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer bystander = fixture.Peer("bystander", fixture.Unsampled);
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(bystander);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);

        BlockImportResult withoutCustodian = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        BlockImportResult sameCustodians = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        fixture.Peers.Add(custodian);
        BlockImportResult custodianConnected = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((withoutCustodian, sameCustodians), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
        Assert.That(custodianConnected, Is.EqualTo(BlockImportResult.Imported), "the newly connected custodian is asked without waiting for the next slot");
        Assert.That(custodian.RootColumnRequests, Is.EqualTo(1));
        Assert.That(bystander.RootColumnRequests, Is.Zero, "a peer custodying no sampled column is never asked");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_block_past_every_peers_recorded_head_gets_its_columns_by_root_from_a_custodian(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong blockSlot = fixture.Chain.Block.Message!.Slot;
        PeerColumnCustody custody = new(fixture.Sampled, isAdvertised: true);
        StubPeer behind = new("behind", blockSlot - 1, static (_, _) => [], custody: custody,
            rootHandler: identifiers => [.. identifiers.Single().Columns!.Select(c => fixture.Chain.Columns[(int)c])]);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(behind);

        BlockImportResult result = await orchestrator.ImportAndSettleAsync(fixture.Importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(blockSlot, Is.GreaterThan(behind.HeadSlot));
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(behind.RootColumnRequests, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_whose_custody_grows_to_a_missing_column_is_asked_in_the_same_slot(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong held = fixture.Sampled[0];
        fixture.SidecarPool.Add(fixture.Chain.BlockRoot, fixture.Chain.Block.Message!.Slot, fixture.Chain.Columns[held]);
        StubPeer holdingOnly = fixture.Peer("peer", [held]);
        StubPeer grown = fixture.Peer("peer", fixture.Sampled);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(holdingOnly);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);

        BlockImportResult custodyingOnlyHeld = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        fixture.Peers[0] = grown;
        BlockImportResult custodyGrown = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(custodyingOnlyHeld, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(custodyGrown, Is.EqualTo(BlockImportResult.Imported), "a peer that custodied only held columns was never asked, so it is asked once it custodies a missing one");
        Assert.That(grown.RootColumnRequests, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_by_root_copy_of_a_column_already_supplied_is_skipped_without_verification(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer honest = fixture.Peer("honest", fixture.Sampled);
        StubPeer late = new("late", fixture.Chain.Block.Message!.Slot, static (_, _) => [], custody: new PeerColumnCustody(fixture.Sampled, isAdvertised: true),
            rootHandler: identifiers => [.. identifiers.Single().Columns!.Select(c => WithoutProofs(fixture.Chain.Columns[(int)c]))]);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.AddRange([honest, late]);

        BlockImportResult result = await orchestrator.ImportAndSettleAsync(fixture.Importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
            Assert.That((honest.RootColumnRequests, late.RootColumnRequests), Is.EqualTo((1, 1)), "both custodians are asked at once");
            Assert.That(late.Reports, Is.Empty, "the unverifiable copies were never verified");
            Assert.That(fixture.Sampled.All(c => fixture.SidecarPool.TryGet(fixture.Chain.BlockRoot, c, out DataColumnSidecar? held) && ReferenceEquals(held, fixture.Chain.Columns[(int)c])),
                Is.True, "the first reply's sidecars stay pooled");
        }

        static DataColumnSidecar WithoutProofs(DataColumnSidecar sidecar) => new()
        {
            Index = sidecar.Index,
            Column = sidecar.Column,
            KzgCommitments = sidecar.KzgCommitments,
            KzgProofs = [],
            SignedBlockHeader = sidecar.SignedBlockHeader,
            KzgCommitmentsInclusionProof = sidecar.KzgCommitmentsInclusionProof,
        };
    }

    public enum CustodianDistribution { SingleColumn, Silent, ConnectsLater, Churns }
    private static IEnumerable<TestCaseData> CustodianRotationCases()
    {
        yield return new TestCaseData(CustodianDistribution.SingleColumn, 0).SetName("Single-column custodians are all reached within bounded imports");
        yield return new TestCaseData(CustodianDistribution.Churns, CustodiansPerImport).SetName("Fresh peer IDs cannot displace an earlier custodian");
        foreach (int count in new[] { 1, 4, 10, 40 })
        {
            yield return new TestCaseData(CustodianDistribution.Silent, count).SetName($"Rotation reaches a serving custodian behind {count} silent custodians");
            yield return new TestCaseData(CustodianDistribution.ConnectsLater, count).SetName($"A newly connected custodian precedes {count} silent custodians");
        }
    }

    [TestCaseSource(nameof(CustodianRotationCases))]
    [CancelAfter(30_000)]
    public async Task Custodian_rotation_reaches_serving_peers_without_repeating_or_multiplying_requests(
        CustodianDistribution distribution, int silentCount, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer[] initial = distribution == CustodianDistribution.SingleColumn
            ? [.. fixture.Sampled.Select(c => fixture.Peer($"custodian-{c}", [c]))]
            : distribution == CustodianDistribution.Churns ? []
            : [.. Enumerable.Range(0, silentCount).Select(i => fixture.SilentPeer($"silent-{i}", fixture.Sampled))];
        StubPeer honest = fixture.Peer("honest", fixture.Sampled);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.AddRange(initial);
        if (distribution == CustodianDistribution.Silent) fixture.Peers.Add(honest);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        List<StubPeer> queried = [.. initial];
        List<BlockImportResult> results = [];
        List<int> requestsPerAttempt = [];
        int custodianCount = distribution == CustodianDistribution.SingleColumn ? initial.Length : silentCount + 1;
        int maxAttempts = (custodianCount + CustodiansPerImport - 1) / CustodiansPerImport;
        int limit = distribution == CustodianDistribution.SingleColumn ? maxAttempts
            : distribution == CustodianDistribution.Churns ? 10 : custodianCount;
        for (int attempt = 0; attempt < limit && (distribution == CustodianDistribution.SingleColumn
            || results.Count == 0 || results[^1] != BlockImportResult.Imported); attempt++)
        {
            if (distribution == CustodianDistribution.ConnectsLater && attempt == 1) fixture.Peers.Add(honest);
            if (distribution == CustodianDistribution.Churns)
            {
                StubPeer[] fresh = [.. Enumerable.Range(0, CustodiansPerImport).Select(i => fixture.SilentPeer($"churned-{queried.Count + i}", fixture.Sampled))];
                queried.AddRange(fresh);
                fixture.Peers.Clear();
                fixture.Peers.AddRange(fresh);
                fixture.Peers.Add(honest);
            }
            int before = queried.Sum(static p => p.RootColumnRequests) + honest.RootColumnRequests;
            results.Add(await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token));
            requestsPerAttempt.Add(queried.Sum(static p => p.RootColumnRequests) + honest.RootColumnRequests - before);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(results[^1], Is.EqualTo(BlockImportResult.Imported), "the rotation reaches the serving custodians in the same slot");
        Assert.That(requestsPerAttempt, Is.All.LessThanOrEqualTo(CustodiansPerImport), "each import asks at most three custodians");
        if (distribution == CustodianDistribution.SingleColumn)
        {
            Assert.That(initial, Has.Length.GreaterThan(CustodiansPerImport));
            Assert.That(results[..^1], Is.All.EqualTo(BlockImportResult.DataUnavailable));
            Assert.That(initial.Select(static p => p.RootColumnRequests), Is.All.EqualTo(1));
        }
        else
        {
            Assert.That(results, distribution is CustodianDistribution.ConnectsLater or CustodianDistribution.Churns
                ? Has.Count.EqualTo(2) : Has.Count.LessThanOrEqualTo(maxAttempts), "a new or previously overlooked custodian is reached on the next import");
            Assert.That(honest.RootColumnRequests, Is.EqualTo(1));
            if (distribution != CustodianDistribution.Churns)
                Assert.That(initial.Select(static p => p.RootColumnRequests), Is.All.LessThanOrEqualTo(1), "a peer is asked once per block and slot");
        }
    }

    [Test]
    public void A_fetch_rotation_remembers_as_asked_only_connected_custodians()
    {
        RangeSync.ColumnFetchRotation rotation = new(ClockAtGenesis(BeaconChainSpec.Mainnet));
        StubPeer stayed = new("stayed", 0, static (_, _) => []);
        List<int> askedCounts = [];
        for (int fetch = 0; fetch < 10; fetch++)
        {
            IBeaconSyncPeer[] custodians = [.. Enumerable.Range(0, CustodiansPerImport).Select(i => new StubPeer($"churned-{fetch}-{i}", 0, static (_, _) => [])), stayed];
            rotation.Take(custodians, CustodiansPerImport);
            askedCounts.Add(rotation.AskedCount);
        }

        Assert.That(askedCounts, Is.All.LessThanOrEqualTo(CustodiansPerImport + 1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_custodian_that_answered_with_no_columns_is_asked_again_in_the_next_slot(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        bool serving = false;
        PeerColumnCustody custody = new(fixture.Sampled, isAdvertised: true);
        StubPeer custodian = new("custodian", fixture.Chain.Block.Message!.Slot, static (_, _) => [], custody: custody,
            rootHandler: identifiers => serving ? [.. identifiers.Single().Columns!.Select(c => fixture.Chain.Columns[(int)c])] : []);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(custodian);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);

        BlockImportResult silentReply = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        serving = true;
        BlockImportResult sameSlot = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        int requestsInFirstSlot = custodian.RootColumnRequests;
        fixture.AdvanceSlots(1);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((silentReply, sameSlot), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
        Assert.That(requestsInFirstSlot, Is.EqualTo(1), "an asked peer is not asked again for the same block in the same slot");
        Assert.That(custodian.RootColumnRequests, Is.EqualTo(2), "the next slot asks it again");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the retried block imports once its columns arrive");
    }

    [TestCase(true, true, TestName = "A_blocks_fetch_rotation_is_forgotten_once_it_imports_or_its_retry_expires(True)")]
    [TestCase(true, false, TestName = "A_blocks_fetch_rotation_is_forgotten_once_it_imports_or_its_retry_expires(False)")]
    [TestCase(false, true, TestName = "A_blocks_pool_watch_ends_once_it_imports_or_its_retry_expires(True)")]
    [TestCase(false, false, TestName = "A_blocks_pool_watch_ends_once_it_imports_or_its_retry_expires(False)")]
    [CancelAfter(30_000)]
    public async Task A_deferred_blocks_tracking_ends_on_import_or_expiry(bool tracksRotation, bool imports, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        if (tracksRotation)
            fixture.Peers.Add(fixture.SilentPeer("silent", fixture.Sampled));
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        if (tracksRotation)
            await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);

        await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        int whileDeferred = tracksRotation ? orchestrator.ColumnFetchRotationCount : fixture.SidecarPool.WatchCount;
        if (imports)
        {
            fixture.Peers.Add(fixture.Peer("custodian", fixture.Sampled));
            await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        }
        else
        {
            fixture.AdvanceSlots(2 * fixture.Chain.Spec.SlotsPerEpoch + 1);
            await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(whileDeferred, Is.EqualTo(1));
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.EqualTo(imports));
        Assert.That(tracksRotation ? orchestrator.ColumnFetchRotationCount : fixture.SidecarPool.WatchCount, Is.Zero);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Custodians_that_never_answer_cost_one_request_timeout_per_import(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        TimeSpan requestTimeout = TimeSpan.FromSeconds(5);
        Gate allAsked = new(CustodiansPerImport);
        UnansweringPeer[] custodians = [.. Enumerable.Range(0, CustodiansPerImport).Select(i => new UnansweringPeer($"unanswering-{i}", fixture.Sampled, allAsked, requestTimeout))];
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.AddRange(custodians);

        Stopwatch elapsed = Stopwatch.StartNew();
        BlockImportResult result = await orchestrator.ImportAndSettleAsync(fixture.Importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        elapsed.Stop();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(allAsked.MaxInFlight, Is.EqualTo(CustodiansPerImport), "every custodian of the import is asked before any answers");
        Assert.That(elapsed.Elapsed, Is.LessThan(requestTimeout), "no request waited out its timeout behind another");
        Assert.That(custodians.Select(static p => p.Failures), Is.All.EqualTo(1), "each failed request penalizes its peer");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_stuck_head_block_is_not_refetched_by_later_rounds_and_imports_once_a_custodian_joins(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        List<ulong> requestedStarts = [];
        StubPeer server = fixture.RangePeer("server", headSlot: fixture.Clock.CurrentSlot, requestedStarts);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(server);
        const int StuckRounds = 3;
        ulong blockSlot = fixture.Chain.Block.Message!.Slot;

        List<int> requestsPerRound = [];
        for (int round = 0; round < StuckRounds; round++)
        {
            int before = server.Requests;
            await orchestrator.FeedRangeSyncRoundAsync(token);
            await orchestrator.ProcessQueuedAsync(token);
            requestsPerRound.Add(server.Requests - before);
        }

        bool stuck = !fixture.Importer.IsKnown(fixture.Chain.BlockRoot);
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.AdmitPeer(custodian);
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(stuck, Is.True, "no peer serves the block's columns");
        Assert.That(requestedStarts.Count(start => start <= blockSlot), Is.EqualTo(1), "only the first round asks for the block's slot");
        Assert.That(requestsPerRound.Skip(1), Is.All.EqualTo(requestsPerRound[0] - 1), "each later round asks for the slots past the block, no more");
        Assert.That(server.RootColumnRequests, Is.EqualTo(1), "the block is not delivered again, so the same custodian is not asked again within the slot");
        Assert.That(custodian.RootColumnRequests, Is.EqualTo(1));
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the admitted custodian is asked at once and the block imports");
        Assert.That(orchestrator.SyncTip, Is.EqualTo((fixture.Chain.BlockRoot, blockSlot)));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_deferred_head_with_held_descendants_makes_no_repeat_block_requests_across_resume_cycles(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        TestLogRecorder log = new();
        HeldChain held = await HoldChainAsync(fixture, slots: [2, 3, 4], token, advance: 10, logs: new OneLoggerLogManager(new ILogger(log)));
        ulong heldSlot = held.Blocks[^1].Slot;
        int firstRoundRequests = held.RequestedStarts.Count;
        int firstRoundBlockRequests = held.RequestedStarts.Count(start => start <= heldSlot);

        List<int> resumed = [];
        foreach (ulong tickSlot in new ulong[] { 74, 106 })
        {
            fixture.AdvanceSlots(tickSlot - fixture.Clock.CurrentSlot);
            await held.Orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
            await held.Orchestrator.FeedRangeSyncRoundAsync(token);
            await held.Orchestrator.ProcessQueuedAsync(token);
            resumed.Add(held.RequestedStarts.Count);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.False, "fixture: the head block waits for its columns");
        Assert.That(log.Messages.Count(static line => line.Contains("resuming range sync from the head")), Is.EqualTo(2), "fixture: the head was resumed twice");
        Assert.That(firstRoundBlockRequests, Is.EqualTo(1), "the first round fetches the held blocks with one request");
        Assert.That(held.RequestedStarts.Count(start => start <= heldSlot), Is.EqualTo(firstRoundBlockRequests), "no later round asks for a held block's slot");
        Assert.That(resumed, Has.Some.GreaterThan(firstRoundRequests), "fixture: the later rounds still ask for the slots above the held blocks");
        Assert.That(held.Orchestrator.PendingGossipBlockCount, Is.EqualTo(held.Blocks.Length - 1), "the descendants wait for the head block");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_held_block_off_the_chain_of_the_peers_is_left_for_the_sync_tip_without_penalizing_them(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ForkedSignedBeaconBlock deferred = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        ForkedSignedBeaconBlock[] canonical = [deferred, .. Fixture.ChainAbove(fixture.Chain.BlockRoot, slots: [2, 3, 4]).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        ForkedSignedBeaconBlock[] forked = [deferred, new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(3, fixture.Chain.BlockRoot))];
        List<ulong> honestStarts = [];
        StubPeer forkPeer = fixture.RangePeer("fork", headSlot: 1000, blocks: forked);
        StubPeer honest = fixture.RangePeer("honest", headSlot: 1000, honestStarts, canonical);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(forkPeer);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);
        int heldFromFork = orchestrator.PendingGossipBlockCount;

        fixture.Peers.Clear();
        fixture.Peers.Add(honest);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);
        int afterRestart = honestStarts.Count;
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldFromFork, Is.EqualTo(1), "fixture: the fork child is held for the deferred block");
        Assert.That(honest.Reports, Is.Empty, "a fork child held here is not the honest peer's fault");
        Assert.That(honestStarts[0], Is.EqualTo(4), "the round first tried the block past the held fork child");
        Assert.That(honestStarts.Skip(1).First(), Is.EqualTo(fixture.Chain.Block.Message!.Slot), "then it started over from the sync tip");
        Assert.That(orchestrator.PendingGossipBlockCount, Is.EqualTo(1 + canonical.Length - 1), "the honest chain is held above the deferred block");
        Assert.That(honestStarts.Skip(afterRestart), Is.All.GreaterThan(canonical[^1].Slot), "and later rounds start past it");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task An_unrelated_dropped_block_does_not_release_the_held_descendants(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldChain held = await HoldChainAsync(fixture, slots: [2, 3, 4], token);
        int heldCount = held.Orchestrator.PendingGossipBlockCount;
        int requestsBefore = held.RequestedStarts.Count;

        BlockImportResult unrelated = await held.Orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(5, fixture.Chain.AnchorRoot)), token);
        await held.Orchestrator.FeedRangeSyncRoundAsync(token);
        await held.Orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(unrelated, Is.EqualTo(BlockImportResult.Invalid), "fixture: the block is dropped");
        Assert.That(held.Orchestrator.PendingGossipBlockCount, Is.EqualTo(heldCount));
        Assert.That(held.RequestedStarts.Skip(requestsBefore), Is.All.GreaterThan(held.Blocks[^1].Slot), "no held block is asked for again");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_block_a_repeated_round_delivers_again_is_held_once(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldChain held = await HoldChainAsync(fixture, slots: [2, 3, 4], token, processRound: false);

        await held.Orchestrator.FeedRangeSyncRoundAsync(token);
        await held.Orchestrator.ProcessQueuedAsync(token);
        held.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(held.Blocks[0]));
        await held.Orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(held.Orchestrator.PendingGossipBlockCount, Is.EqualTo(held.Blocks.Length - 1));
        Assert.That(held.Orchestrator.RangeHeldSlot, Is.EqualTo(held.Blocks[^1].Slot));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_round_that_falls_back_to_the_sync_tip_rebuilds_the_held_chain(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldChain held = await HoldChainAsync(fixture, slots: [2, 3, 4], token);
        ForkedSignedBeaconBlock reorg = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(5, held.Blocks[2].ComputeMessageRoot()));
        List<ulong> starts = [];
        StubPeer peer = fixture.RangePeer("reorg", headSlot: 1000, starts, [.. held.Blocks[..3], reorg]);
        fixture.Peers.Clear();
        fixture.Peers.Add(peer);

        await held.Orchestrator.FeedRangeSyncRoundAsync(token);
        await held.Orchestrator.ProcessQueuedAsync(token);
        int fallbackRequests = starts.Count;
        await held.Orchestrator.FeedRangeSyncRoundAsync(token);
        await held.Orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(starts.Take(fallbackRequests), Has.Some.EqualTo(held.Blocks[0].Slot), "fixture: the round fell back to the sync tip");
        Assert.That(peer.Reports, Is.Empty);
        Assert.That(held.Orchestrator.RangeHeldSlot, Is.EqualTo(reorg.Slot));
        Assert.That(starts.Skip(fallbackRequests), Is.All.GreaterThan(reorg.Slot), "no held block is asked for again");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_whose_blocks_break_their_chain_past_the_held_tip_is_penalized([Values] bool inLaterBatch, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldChain held = await HoldChainAsync(fixture, slots: [2, 3], token);
        ForkedSignedBeaconBlock next = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(4, held.Blocks[^1].ComputeMessageRoot()));
        ulong brokenSlot = inLaterBatch ? next.Slot + RangeSync.DefaultBatchSize : next.Slot + 1;
        ForkedSignedBeaconBlock broken = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(brokenSlot, Keccak.Compute("elsewhere")));
        ForkedSignedBeaconBlock[] served = [.. held.Blocks, next, broken];
        bool brokenServed = false;
        StubPeer peer = new("breaking", headSlot: 1000, (start, count) =>
            {
                ForkedSignedBeaconBlock[] batch = [.. served.Where(b => b.Slot >= start && b.Slot < start + count && (b != broken || !brokenServed))];
                brokenServed |= batch.Contains(broken);
                return batch;
            },
            custody: new PeerColumnCustody(fixture.Sampled, isAdvertised: true), rootHandler: static _ => []);
        fixture.Peers.Clear();
        fixture.Peers.Add(peer);

        await held.Orchestrator.FeedRangeSyncRoundAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(brokenServed, Is.True, "fixture: the broken block was served");
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Range_blocks_held_for_a_deferred_head_stop_at_their_share_of_the_queue(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldChain held = await HoldChainAsync(fixture, slots: [.. Enumerable.Range(2, BeaconSyncOrchestrator.MaxRangeHeldBlocks + 6).Select(static slot => (ulong)slot)], token, advance: 100);
        int heldCount = held.Orchestrator.PendingGossipBlockCount;
        int requestsBefore = held.Server.Requests;

        await held.Orchestrator.FeedRangeSyncRoundAsync(token);
        await held.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(fixture.Clock.CurrentSlot, Keccak.Compute("unknown parent"))), token);
        int gossipHeld = held.Orchestrator.PendingGossipBlockCount;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldCount, Is.EqualTo(BeaconSyncOrchestrator.MaxRangeHeldBlocks));
        Assert.That(held.Server.Requests, Is.EqualTo(requestsBefore), "a full share makes the next round ask for nothing");
        Assert.That(gossipHeld, Is.EqualTo(heldCount + 1), "a gossip block still finds room in the queue");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task The_held_chain_ends_when_its_blocks_import_and_passes_to_a_child_that_waits_in_turn([Values] bool childWaits, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), .. Fixture.ChainAbove(fixture.Chain.BlockRoot, slots: [2, 3, 4]).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        List<ulong> requestedStarts = [];
        fixture.Peers.Add(fixture.RangePeer("server", headSlot: 1000, requestedStarts, blocks));
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers: 0);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);
        ulong? heldBefore = orchestrator.RangeHeldSlot;
        (childWaits ? importer.Stuck : importer.Accepted).Add(blocks[1].ComputeMessageRoot());
        importer.Accepted.UnionWith(blocks[2..].Select(static b => b.ComputeMessageRoot()));

        fixture.AdmitPeer(fixture.Peer("custodian", fixture.Sampled));
        await orchestrator.SettleColumnFetchesAsync(token);
        int requestsBefore = requestedStarts.Count;
        await orchestrator.FeedRangeSyncRoundAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldBefore, Is.EqualTo(blocks[^1].Slot), "fixture: the blocks are held for the deferred block");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "fixture: the deferred block imported");
        Assert.That(orchestrator.RangeHeldSlot, Is.EqualTo(childWaits ? blocks[^1].Slot : null));
        Assert.That(requestedStarts.Skip(requestsBefore), Is.All.GreaterThan(blocks[^1].Slot), "no held block is asked for again");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Blocks_behind_a_deferred_block_import_once_its_columns_arrive_by_root([Values] bool laterHeldBlockWaits, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldChainDrain drain = await DrainHeldChainAsync(fixture, laterHeldBlockWaits, token);
        int heldBehindLaterBlock = drain.Orchestrator.PendingGossipBlockCount;
        ulong? heldSlot = drain.Orchestrator.RangeHeldSlot;

        if (laterHeldBlockWaits)
        {
            drain.Importer.Stuck.Clear();
            fixture.AdvanceSlots(1);
            await drain.Orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        }

        ForkedSignedBeaconBlock last = drain.Blocks[^1];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "fixture: the deferred block imported once its columns arrived");
        Assert.That(heldBehindLaterBlock, Is.EqualTo(laterHeldBlockWaits ? drain.Blocks.Length - WaitingIndex - 1 : 0), "the blocks behind the waiting block, of both batches, are held");
        Assert.That(heldSlot, Is.EqualTo(laterHeldBlockWaits ? last.Slot : null), "rounds keep starting past the held blocks");
        Assert.That(drain.Blocks.Skip(1).Select(static b => b.ComputeMessageRoot()), Is.All.Matches<Hash256>(drain.Importer.IsKnown), "every block of both batches imports");
        Assert.That(drain.Orchestrator.SyncTip, Is.EqualTo((last.ComputeMessageRoot(), last.Slot)));
        Assert.That(drain.Orchestrator.PendingGossipBlockCount, Is.Zero);
        Assert.That(drain.Orchestrator.RangeHeldSlot, Is.Null);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Blocks_behind_a_held_block_that_never_gets_its_data_are_fetched_again_once_its_retry_expires(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        TestLogRecorder log = new();
        HeldChainDrain drain = await DrainHeldChainAsync(fixture, laterHeldBlockWaits: true, token, new OneLoggerLogManager(new ILogger(log)));
        ForkedSignedBeaconBlock waiting = drain.Blocks[WaitingIndex];
        Hash256 waitingRoot = waiting.ComputeMessageRoot();

        fixture.AdvanceSlots(1);
        await drain.Orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        drain.RequestedStarts.Clear();
        await drain.Orchestrator.FeedRangeSyncRoundAsync(token);
        await drain.Orchestrator.ProcessQueuedAsync(token);
        List<ulong> whileRetried = [.. drain.RequestedStarts];
        int heldWhileRetried = drain.Orchestrator.PendingGossipBlockCount;

        fixture.AdvanceSlots(2 * fixture.Chain.Spec.SlotsPerEpoch + 1);
        await drain.Orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        int heldAfterExpiry = drain.Orchestrator.PendingGossipBlockCount;
        ulong? heldSlotAfterExpiry = drain.Orchestrator.RangeHeldSlot;
        drain.RequestedStarts.Clear();
        await drain.Orchestrator.FeedRangeSyncRoundAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(drain.Importer.ImportCalls.Count(root => root == waitingRoot), Is.GreaterThan(1), "the waiting block is retried on the slot tick");
        Assert.That(heldWhileRetried, Is.EqualTo(drain.Blocks.Length - WaitingIndex - 1), "the blocks behind it stay held while it is retried");
        Assert.That(whileRetried, Is.Not.Empty.And.All.GreaterThan(drain.Blocks[^1].Slot), "no round asks for a held block while it is retried");
        Assert.That(log.Messages.Count(line => line.Contains($"Dropping block {waitingRoot}")), Is.EqualTo(1), "the drop is logged");
        Assert.That(heldAfterExpiry, Is.Zero, "the blocks behind it are dropped with it");
        Assert.That(heldSlotAfterExpiry, Is.Null);
        Assert.That(drain.RequestedStarts, Has.Some.LessThanOrEqualTo(waiting.Slot), "the next round asks for the dropped block again");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Blocks_held_behind_an_invalid_block_are_dropped_with_one_log_line_and_fetched_again(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        TestLogRecorder log = new();
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), .. Fixture.ChainAbove(fixture.Chain.BlockRoot, slots: [2, 3, 4, 5]).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        List<ulong> requestedStarts = [];
        fixture.Peers.Add(fixture.RangePeer("server", headSlot: 1000, requestedStarts, blocks));
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter _) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers: 0, new OneLoggerLogManager(new ILogger(log)));
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);
        int heldBefore = orchestrator.PendingGossipBlockCount;
        ulong droppedBefore = Metrics.BeaconChainHeldBlocksDropped;

        fixture.AdmitPeer(fixture.Peer("custodian", fixture.Sampled));
        await orchestrator.SettleColumnFetchesAsync(token);
        requestedStarts.Clear();
        await orchestrator.FeedRangeSyncRoundAsync(token);

        string[] drops = [.. log.Messages.Where(static line => line.StartsWith("Dropped "))];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldBefore, Is.EqualTo(blocks.Length - 1), "fixture: the blocks above the deferred block are held");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "fixture: the deferred block imported");
        Assert.That(drops, Has.Length.EqualTo(1), "one line for the whole chain");
        Assert.That(drops[0], Does.Contain($"the {blocks.Length - 2} held blocks behind {blocks[1].ComputeMessageRoot()}").And.Contain("invalid"));
        Assert.That(log.Messages, Has.None.Contain("Exception"));
        Assert.That(Metrics.BeaconChainHeldBlocksDropped, Is.GreaterThanOrEqualTo(droppedBefore + (ulong)(blocks.Length - 2)), "the drop is counted");
        Assert.That(orchestrator.PendingGossipBlockCount, Is.Zero);
        Assert.That(requestedStarts, Has.Some.EqualTo(blocks[1].Slot), "the next round fetches the dropped blocks again");
    }

    [TestCase(true, TestName = "Blocks_held_behind_a_block_the_full_retry_set_refuses_are_dropped_with_one_log_line")]
    [TestCase(false, TestName = "A_held_tip_the_full_retry_set_refuses_is_fetched_again_by_the_next_round")]
    [CancelAfter(60_000)]
    public async Task A_block_the_full_retry_set_refuses_is_fetched_again_by_the_next_round(bool hasDescendant, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        TestLogRecorder log = new();
        ForkedSignedBeaconBlock head = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        ForkedSignedBeaconBlock first = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(2, fixture.Chain.BlockRoot));
        // A fork of the first block: the deferred head's import re-drives both, and only one finds room in the retry set.
        ForkedSignedBeaconBlock refused = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(3, fixture.Chain.BlockRoot));
        ForkedSignedBeaconBlock[] blocks = hasDescendant
            ? [head, first, refused, new ForkedSignedBeaconBlock.OfFulu(Fixture.ChainAbove(refused.ComputeMessageRoot(), slots: [4])[0])]
            : [head, first, refused];
        List<ulong> requestedStarts = [];
        fixture.Peers.Add(fixture.RangePeer("server", headSlot: 1000, requestedStarts, [head, first]));
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers: RetrySetCapacity - 1,
            logs: hasDescendant ? new OneLoggerLogManager(new ILogger(log)) : null);
        importer.Stuck.UnionWith([first.ComputeMessageRoot(), refused.ComputeMessageRoot()]);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(refused));
        if (hasDescendant)
        {
            orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(blocks[3]));
        }
        await orchestrator.ProcessQueuedAsync(token);
        int heldBefore = orchestrator.PendingGossipBlockCount;

        fixture.AdmitPeer(fixture.Peer("custodian", fixture.Sampled));
        await orchestrator.SettleColumnFetchesAsync(token);
        requestedStarts.Clear();
        await orchestrator.FeedRangeSyncRoundAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldBefore, Is.EqualTo(blocks.Length - 1), "fixture: the blocks above the deferred head are held");
        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity), "fixture: the first block took the last place");
        Assert.That(requestedStarts, Has.Some.EqualTo(refused.Slot), "the next round starts at the refused block, not past it");
        if (hasDescendant)
        {
            string[] drops = [.. log.Messages.Where(static line => line.StartsWith("Dropped "))];
            Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "fixture: the deferred head imported");
            Assert.That(drops, Has.Length.EqualTo(1));
            Assert.That(drops[0], Does.Contain($"the 1 held block behind {refused.ComputeMessageRoot()}").And.Contain("retry set is full"));
            Assert.That(log.Messages, Has.None.Contain("Exception"));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Blocks_held_behind_a_deferred_block_fetch_their_columns_at_once_up_to_a_bound(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        const int HeldBlocks = BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches + 4;
        HeldBlobBlocks scenario = await HoldBlobBlocksBehindDeferredHeadAsync(fixture, HeldBlocks, (importer, delivered) => importer.AcceptOnceColumnsAreHeld(delivered, fixture.SidecarPool, fixture.Sampled), token);
        await ProcessUntilAsync(scenario.Orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);
        int queuedForAPlace = scenario.Orchestrator.HeldColumnFetchQueueCount;
        int started = HeldBlocks - queuedForAPlace;
        await scenario.Gate.WaitForRequestsAsync(started, token);
        int requestedWhileBlocked = scenario.HeldRootsRequested.Length;
        int inFlightWhileBlocked = scenario.Gate.MaxInFlight;
        int heldAfterHeadImported = scenario.Orchestrator.PendingGossipBlockCount;

        scenario.Gate.Open();
        await ProcessUntilAsync(scenario.Orchestrator, () => scenario.HeldRootsRequested.Length == HeldBlocks, token);
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);
        int inFlightAtMost = scenario.Gate.MaxInFlight;
        bool columnsHeld = scenario.HeldRoots.All(root => fixture.Sampled.All(column => fixture.SidecarPool.TryGet(root, column, out _)));

        fixture.AdvanceSlots(1);
        await scenario.Orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "fixture: the deferred block imported");
        Assert.That(heldAfterHeadImported, Is.EqualTo(HeldBlocks - 1), "fixture: the blocks behind the first one it drains wait for their parents");
        Assert.That(queuedForAPlace, Is.EqualTo(HeldBlocks - BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches), "the blocks past the bound wait for a place");
        Assert.That(requestedWhileBlocked, Is.EqualTo(BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches), "only the blocks with a place asked the peer");
        Assert.That(inFlightWhileBlocked, Is.EqualTo(BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches), "the fetches of the held blocks overlap, up to the bound");
        Assert.That(inFlightAtMost, Is.EqualTo(BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches), "a finished fetch gives its place to one queued block, never more");
        Assert.That(columnsHeld, Is.True, "every held block's columns reached the pool");
        Assert.That(scenario.HeldRoots, Is.All.Matches<Hash256>(scenario.Importer.IsKnown), "every held block imports");
        Assert.That(scenario.Orchestrator.PendingGossipBlockCount, Is.Zero);
        Assert.That(scenario.Orchestrator.PendingRetryBlockCount, Is.Zero);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task The_pool_gated_importer_keeps_a_block_held_whose_columns_never_reached_the_pool(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldBlobBlocks scenario = await HoldBlobBlocksBehindDeferredHeadAsync(fixture, 2, (importer, delivered) => importer.AcceptOnceColumnsAreHeld(delivered, fixture.SidecarPool, fixture.Sampled), token, columnsArrive: false);
        await ProcessUntilAsync(scenario.Orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);

        scenario.Gate.Open();
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);
        fixture.AdvanceSlots(1);
        await scenario.Orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);

        Assert.That(scenario.HeldRoots, Is.All.Matches<Hash256>(root => !scenario.Importer.IsKnown(root)), "no column reached the pool, so no block imports");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_held_block_that_imports_before_its_turn_for_a_column_fetch_is_not_fetched(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        const int HeldBlocks = BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches + 4;
        HeldBlobBlocks scenario = await HoldBlobBlocksBehindDeferredHeadAsync(fixture, HeldBlocks, static (importer, delivered) =>
        {
            importer.Accepted.UnionWith(delivered[..^1]);
            importer.Stuck.Add(delivered[^1]);
        }, token);
        await ProcessUntilAsync(scenario.Orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);
        Hash256[] importedBeforeTheirTurn = scenario.HeldRoots[BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches..^1];

        scenario.Gate.Open();
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(scenario.HeldRoots[..^1], Is.All.Matches<Hash256>(scenario.Importer.IsKnown), "fixture: the blocks before the last one imported");
        Assert.That(importedBeforeTheirTurn, Is.Not.Empty.And.None.Matches<Hash256>(scenario.Peer.RequestedRoots.Contains), "no block asks for columns it no longer waits on");
        Assert.That(scenario.HeldRootsRequested, Is.EquivalentTo(scenario.HeldRoots[..BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches].Append(scenario.HeldRoots[^1])), "the blocks that had a place, and the one that waits");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Held_blocks_dropped_before_their_column_fetch_started_leave_the_fetch_queue(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        const int HeldBlocks = BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches + 4;
        TestLogRecorder log = new();
        // A sibling of the held chain takes the last place of the retry set first, so the chain's first block is refused and the blocks behind it are dropped.
        ForkedSignedBeaconBlock sibling = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(2, fixture.Chain.BlockRoot));
        HeldBlobBlocks scenario = await HoldBlobBlocksBehindDeferredHeadAsync(fixture, HeldBlocks, static (importer, delivered) => importer.Stuck.UnionWith(delivered), token,
            fillers: RetrySetCapacity - 1, logs: new OneLoggerLogManager(new ILogger(log)), deliveredBefore: [sibling]);
        await ProcessUntilAsync(scenario.Orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);
        int queuedAfterDrop = scenario.Orchestrator.HeldColumnFetchQueueCount;

        scenario.Gate.Open();
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);

        string[] drops = [.. log.Messages.Where(static line => line.StartsWith("Dropped "))];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(scenario.Orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity), "fixture: the sibling took the last place");
        Assert.That(drops, Has.Length.EqualTo(1), "fixture: the blocks behind the refused block were dropped");
        Assert.That(queuedAfterDrop, Is.Zero);
        Assert.That(scenario.HeldRootsRequested, Is.EquivalentTo(scenario.HeldRoots[..BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches]), "only the fetches that had started");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task The_fetch_queue_is_empty_once_the_held_chain_ends(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        const int HeldBlocks = BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches + 4;
        HeldBlobBlocks scenario = await HoldBlobBlocksBehindDeferredHeadAsync(fixture, HeldBlocks, static (importer, delivered) => importer.Accepted.UnionWith(delivered), token);
        await ProcessUntilAsync(scenario.Orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(scenario.HeldRoots, Is.All.Matches<Hash256>(scenario.Importer.IsKnown), "fixture: every held block imported with the deferred block");
        Assert.That(scenario.Orchestrator.RangeHeldSlot, Is.Null, "fixture: the chain ended");
        Assert.That(scenario.Orchestrator.HeldColumnFetchQueueCount, Is.Zero);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_held_block_that_defers_while_its_own_fetch_runs_asks_again_when_that_fetch_ends_short(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldBlobBlocks scenario = await HoldBlobBlocksBehindDeferredHeadAsync(fixture, 2, static (importer, delivered) => importer.Stuck.UnionWith(delivered), token, columnsArrive: false);
        await ProcessUntilAsync(scenario.Orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);
        Hash256 first = scenario.HeldRoots[0];
        int askedWhileRunning = scenario.Peer.RequestedRoots.Count(root => root == first);

        scenario.Gate.Open();
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(scenario.Orchestrator.PendingRetryBlockCount, Is.EqualTo(1), "fixture: the first held block waits for its data");
        Assert.That(askedWhileRunning, Is.EqualTo(1), "fixture: its own fetch was still running");
        Assert.That(scenario.Peer.RequestedRoots.Count(root => root == first), Is.EqualTo(2), "the short fetch is followed by another without a slot tick");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_refused_block_whose_columns_a_held_fetch_is_fetching_gets_its_import_attempt(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        SignedBeaconBlock fork = Test.P2P.TestChain.CreateBlock(3, fixture.Chain.BlockRoot);
        fork.Message!.Body!.BlobKzgCommitments = fixture.Chain.Block.Message!.Body!.BlobKzgCommitments;
        ForkedSignedBeaconBlock refused = new ForkedSignedBeaconBlock.OfFulu(fork);
        // The first held block takes the last place of the retry set once the head leaves it, so its fork is refused.
        HeldBlobBlocks scenario = await HoldBlobBlocksBehindDeferredHeadAsync(fixture, 1, static (importer, delivered) => importer.Stuck.UnionWith(delivered), token, fillers: RetrySetCapacity - 1, deliveredAfter: [refused]);
        await ProcessUntilAsync(scenario.Orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);
        Hash256 refusedRoot = refused.ComputeMessageRoot();
        int retrySetAfterDrain = scenario.Orchestrator.PendingRetryBlockCount;
        bool importedBeforeItsFetchEnded = scenario.Importer.IsKnown(refusedRoot);
        scenario.Importer.Stuck.Remove(refusedRoot);
        scenario.Importer.Accepted.Add(refusedRoot);

        scenario.Gate.Open();
        await scenario.Orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(retrySetAfterDrain, Is.EqualTo(RetrySetCapacity), "fixture: the retry set is full, so the fork is refused");
        Assert.That(importedBeforeItsFetchEnded, Is.False);
        Assert.That(scenario.Importer.IsKnown(refusedRoot), Is.True, "the refused block is imported once the fetch of its columns ends");
        Assert.That(scenario.Orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity), "the set stays at its cap");
    }

    private sealed record HeldBlobBlocks(BeaconSyncOrchestrator Orchestrator, StuckBlocksImporter Importer, GatedColumnPeer Peer, Gate Gate, ForkedSignedBeaconBlock[] Held)
    {
        public Hash256[] HeldRoots => [.. Held.Select(static b => b.ComputeMessageRoot())];

        public Hash256[] HeldRootsRequested => [.. Peer.RequestedRoots.Distinct().Intersect(HeldRoots)];
    }

    /// <summary>The head fetch completes immediately; held-child fetches wait at the gate.</summary>
    private static async Task<HeldBlobBlocks> HoldBlobBlocksBehindDeferredHeadAsync(
        Fixture fixture,
        int heldCount,
        Action<StuckBlocksImporter, Hash256[]> script,
        CancellationToken token,
        bool columnsArrive = true,
        int fillers = 0,
        ILogManager? logs = null,
        ForkedSignedBeaconBlock[]? deliveredBefore = null,
        ForkedSignedBeaconBlock[]? deliveredAfter = null)
    {
        ForkedSignedBeaconBlock head = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        ForkedSignedBeaconBlock[] held = BlobChainAbove(fixture.Chain.BlockRoot, heldCount, fixture.Chain.Block.Message!.Body!.BlobKzgCommitments!, firstSlot: deliveredBefore is null ? 2UL : 3UL);
        ForkedSignedBeaconBlock[] delivered = [.. deliveredBefore ?? [], .. held, .. deliveredAfter ?? []];
        Dictionary<Hash256, ulong> slots = delivered.ToDictionary(static b => b.ComputeMessageRoot(), static b => b.Slot);
        Gate gate = new(int.MaxValue);
        Action<Hash256> addColumns = root =>
        {
            foreach (ulong column in fixture.Sampled)
            {
                fixture.SidecarPool.Add(root, slots[root], fixture.Chain.Columns[(int)column]);
            }
        };
        GatedColumnPeer peer = new(fixture.Sampled, gate, fixture.Chain.BlockRoot, fixture.Chain.Columns, columnsArrive ? addColumns : null);
        fixture.Peers.Add(peer);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers, logs);
        script(importer, [.. delivered.Select(static b => b.ComputeMessageRoot())]);

        foreach (ForkedSignedBeaconBlock block in (ForkedSignedBeaconBlock[])[head, .. delivered])
        {
            orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(block));
        }

        await orchestrator.ProcessQueuedAsync(token);
        int expectedInFlight = Math.Min(delivered.Length, BeaconSyncOrchestrator.MaxConcurrentHeldColumnFetches);
        await gate.WaitForRequestsAsync(expectedInFlight, token);

        return new HeldBlobBlocks(orchestrator, importer, peer, gate, held);
    }

    private static ForkedSignedBeaconBlock[] BlobChainAbove(Hash256 parentRoot, int count, SszKzgCommitment[] commitments, ulong firstSlot = 2)
    {
        List<ForkedSignedBeaconBlock> chain = [];
        for (int i = 0; i < count; i++)
        {
            SignedBeaconBlock block = Test.P2P.TestChain.CreateBlock(firstSlot + (ulong)i, parentRoot);
            block.Message!.Body!.BlobKzgCommitments = commitments;
            ForkedSignedBeaconBlock forked = new ForkedSignedBeaconBlock.OfFulu(block);
            chain.Add(forked);
            parentRoot = forked.ComputeMessageRoot();
        }

        return [.. chain];
    }

    private sealed class GatedColumnPeer(ulong[] custodied, Gate gate, Hash256 servedRoot, DataColumnSidecar[] servedColumns, Action<Hash256>? afterGate) : TestPeer("gated", ulong.MaxValue, new(custodied, isAdvertised: true))
    {
        private readonly ConcurrentQueue<Hash256> _requestedRoots = new();

        public IEnumerable<Hash256> RequestedRoots => _requestedRoots;

        public override async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
        {
            DataColumnsByRootIdentifier identifier = identifiers.Single();
            _requestedRoots.Enqueue(identifier.BlockRoot!);
            if (identifier.BlockRoot == servedRoot)
            {
                return [.. identifier.Columns!.Select(c => servedColumns[(int)c])];
            }

            await gate.WaitAsync(TimeSpan.FromSeconds(30), token);
            afterGate?.Invoke(identifier.BlockRoot!);
            return [];
        }
    }

    private const int WaitingIndex = 2;

    private const int FirstBatchLength = 5;

    private sealed record HeldChainDrain(BeaconSyncOrchestrator Orchestrator, StuckBlocksImporter Importer, List<ulong> RequestedStarts, ForkedSignedBeaconBlock[] Blocks);

    private static async Task<HeldChainDrain> DrainHeldChainAsync(Fixture fixture, bool laterHeldBlockWaits, CancellationToken token, ILogManager? logs = null)
    {
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), .. Fixture.ChainAbove(fixture.Chain.BlockRoot, slots: [2, 3, 4, 5, 6, 7, 8]).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        List<ulong> requestedStarts = [];
        fixture.Peers.Add(fixture.RangePeer("server", headSlot: 1000, requestedStarts, blocks[..FirstBatchLength]));
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers: 0, logs);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);
        Assert.That(orchestrator.PendingGossipBlockCount, Is.EqualTo(FirstBatchLength - 1), "fixture: the batch is held behind its deferred first block");

        importer.Accepted.UnionWith(blocks[1..].Select(static b => b.ComputeMessageRoot()));
        if (laterHeldBlockWaits)
        {
            importer.Stuck.Add(blocks[WaitingIndex].ComputeMessageRoot());
        }

        fixture.AdmitPeer(fixture.Peer("custodian", fixture.Sampled));
        await orchestrator.SettleColumnFetchesAsync(token);
        foreach (ForkedSignedBeaconBlock block in blocks[FirstBatchLength..])
        {
            orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(block));
        }

        await orchestrator.ProcessQueuedAsync(token);
        return new HeldChainDrain(orchestrator, importer, requestedStarts, blocks);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_deferred_block_another_route_imported_ends_the_held_chain(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        HeldChain held = await HoldChainAsync(fixture, slots: [2, 3], token);
        foreach (ulong column in fixture.Sampled)
        {
            fixture.GiveColumn(column);
        }

        BlockImportResult otherRoute = fixture.Importer.Import(held.Blocks[0], held.Blocks[0].ComputeMessageRoot(), verifySignatures: true);
        await held.Orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(otherRoute, Is.EqualTo(BlockImportResult.Imported), "fixture: the block imports once its columns are held");
        Assert.That(held.Orchestrator.PendingRetryBlockCount, Is.Zero, "fixture: the retry ended");
        Assert.That(held.Orchestrator.RangeHeldSlot, Is.Null);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_block_that_follows_no_held_or_deferred_block_is_not_held(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        ForkedSignedBeaconBlock stray = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(3, Keccak.Compute("nobody")));

        orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(stray));
        await orchestrator.ProcessQueuedAsync(token);

        Assert.That(orchestrator.PendingGossipBlockCount, Is.Zero);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_deferred_block_the_full_retry_set_refuses_is_not_treated_as_held(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        List<ulong> requestedStarts = [];
        fixture.Peers.Add(fixture.RangePeer("server", headSlot: 1000, requestedStarts));
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token);
        importer.Stuck.Add(fixture.Chain.BlockRoot);
        orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block)));
        await orchestrator.SettleColumnFetchesAsync(token);

        await orchestrator.FeedRangeSyncRoundAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity), "fixture: the retry set refused the block");
        Assert.That(requestedStarts, Has.Some.EqualTo(fixture.Chain.Block.Message!.Slot), "the round asks for the refused block again");
    }

    public enum OffChainWait
    {
        None,
        Block,

        ChildBelowHeldTip,
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Blocks_held_above_a_deferred_head_are_fetched_again_once_the_head_is_dropped([Values] OffChainWait offChain, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        SignedBeaconBlock[] chain = Fixture.ChainAbove(fixture.Chain.BlockRoot, slots: [2, 3]);
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), .. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        List<ulong> requestedStarts = [];
        StubPeer server = fixture.RangePeer("server", headSlot: 1000, requestedStarts, blocks);
        fixture.Peers.Add(server);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers: 0);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);
        requestedStarts.Clear();
        BlockImportResult unrelated = BlockImportResult.DataUnavailable;
        if (offChain != OffChainWait.None)
        {
            // Deferred an epoch later, so it still waits when the head block's retry expires.
            fixture.AdvanceSlots(fixture.Chain.Spec.SlotsPerEpoch);
            ForkedSignedBeaconBlock offChainBlock = SecondBlobBlock(fixture);
            unrelated = await orchestrator.ImportBlockAsync(offChainBlock, token);
            await orchestrator.SettleColumnFetchesAsync(token);
            if (offChain == OffChainWait.ChildBelowHeldTip)
            {
                ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(blocks[^1].Slot, offChainBlock.ComputeMessageRoot()));
                importer.Stuck.Add(child.ComputeMessageRoot());
                orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(child));
                await orchestrator.ProcessQueuedAsync(token);
                importer.Accepted.Add(offChainBlock.ComputeMessageRoot());
                fixture.AdvanceSlots(1);
                await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
                fixture.AdvanceSlots(fixture.Chain.Spec.SlotsPerEpoch);
            }
            else
            {
                fixture.AdvanceSlots(fixture.Chain.Spec.SlotsPerEpoch + 1);
            }
        }
        else
        {
            fixture.AdvanceSlots(2 * fixture.Chain.Spec.SlotsPerEpoch + 1);
        }

        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.FeedRangeSyncRoundAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(unrelated, Is.EqualTo(BlockImportResult.DataUnavailable), "fixture: the unrelated block waits for its data");
        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(offChain == OffChainWait.None ? 0 : 1), "fixture: only the head block's retry expired");
        Assert.That(orchestrator.RangeHeldSlot, Is.Null);
        Assert.That(orchestrator.PendingGossipBlockCount, Is.Zero);
        Assert.That(requestedStarts.Min(), Is.EqualTo(fixture.Chain.Block.Message!.Slot), "the dropped blocks are fetched again from the head");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_block_of_the_previous_held_chain_does_not_take_over_the_next_one(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ForkedSignedBeaconBlock[] previous = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), .. Fixture.ChainAbove(fixture.Chain.BlockRoot, slots: [2, 3]).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        List<ulong> requestedStarts = [];
        fixture.Peers.Add(fixture.RangePeer("server", headSlot: 1000, requestedStarts, previous));
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers: 0);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);

        ForkedSignedBeaconBlock deferred = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(previous[^1].Slot + 1, fixture.Chain.AnchorRoot));
        ForkedSignedBeaconBlock heldAbove = new ForkedSignedBeaconBlock.OfFulu(Fixture.ChainAbove(deferred.ComputeMessageRoot(), slots: [deferred.Slot + 1])[0]);
        importer.Stuck.Add(deferred.ComputeMessageRoot());
        orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(deferred));
        orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(heldAbove));
        await orchestrator.ProcessQueuedAsync(token);
        ulong? heldSlot = orchestrator.RangeHeldSlot;

        fixture.AdvanceSlots(1);
        importer.Stuck.Add(previous[1].ComputeMessageRoot());
        importer.Accepted.Add(previous[2].ComputeMessageRoot());
        fixture.AdmitPeer(fixture.Peer("custodian", fixture.Sampled));
        await orchestrator.SettleColumnFetchesAsync(token);

        fixture.AdvanceSlots(2 * fixture.Chain.Spec.SlotsPerEpoch);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        requestedStarts.Clear();
        await orchestrator.FeedRangeSyncRoundAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldSlot, Is.EqualTo(heldAbove.Slot), "fixture: the later round holds blocks above its own deferred block");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "fixture: the previous chain's deferred block imported");
        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(1), "fixture: the previous chain's block still waits");
        Assert.That(orchestrator.RangeHeldSlot, Is.Null);
        Assert.That(requestedStarts, Has.Some.LessThanOrEqualTo(deferred.Slot), "the dropped blocks of the later round are fetched again");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_custodian_admitted_between_ticks_gets_the_by_root_fetch_at_once([Values] bool custodies, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        BlockImportResult withoutCustodian = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        StubPeer admitted = fixture.Peer("admitted", custodies ? fixture.Sampled : fixture.Unsampled);

        fixture.AdmitPeer(admitted);
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(withoutCustodian, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(admitted.RootColumnRequests, Is.EqualTo(custodies ? 1 : 0));
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.EqualTo(custodies), "the block imports before the next slot tick");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_admitted_without_a_missing_column_costs_no_request([Values] bool custodiesOnlyAHeldColumn, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        ulong held = fixture.Sampled[0];
        fixture.GiveColumn(held);
        // More silent custodians of the missing columns than one fetch asks, so an extra fetch would reach one.
        StubPeer[] silent = [.. Enumerable.Range(0, CustodiansPerImport + 1).Select(i => fixture.SilentPeer($"silent-{i}", fixture.Sampled[1..]))];
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.AddRange(silent);
        await orchestrator.ImportAndSettleAsync(fixture.Importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        int beforeAdmission = silent.Sum(static p => p.RootColumnRequests);

        fixture.AdmitPeer(fixture.Peer("admitted", custodiesOnlyAHeldColumn ? [held] : fixture.Unsampled));
        await orchestrator.SettleColumnFetchesAsync(token);

        Assert.That((beforeAdmission, silent.Sum(static p => p.RootColumnRequests)), Is.EqualTo((CustodiansPerImport, CustodiansPerImport)));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_custodian_admitted_during_a_running_fetch_is_asked_when_that_fetch_ends_short(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        Gate slowFetch = new(expected: int.MaxValue);
        fixture.Peers.Add(new UnansweringPeer("slow", fixture.Sampled, slowFetch, TimeSpan.FromSeconds(20)));
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        while (slowFetch.MaxInFlight == 0)
        {
            await Task.Delay(10, token);
        }

        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.AdmitPeer(custodian);
        await orchestrator.ProcessQueuedAsync(token);
        int askedWhileRunning = custodian.RootColumnRequests;
        slowFetch.Open();
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(askedWhileRunning, Is.Zero, "fixture: the running fetch had not reached the custodian");
        Assert.That(custodian.RootColumnRequests, Is.EqualTo(1));
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the block imports without a slot tick");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_running_worker_fetches_for_an_admitted_custodian_and_leaves_the_pool_when_it_stops(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator(routeAdmissions: false);
        await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task worker = orchestrator.RunWorkerAsync(stop.Token);
        int subscribedWhileRunning = fixture.AdmissionSubscribers;

        fixture.AdmitPeer(fixture.Peer("custodian", fixture.Sampled));
        while (!fixture.Importer.IsKnown(fixture.Chain.BlockRoot))
        {
            await Task.Delay(10, token);
        }

        await stop.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await worker);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(subscribedWhileRunning, Is.EqualTo(1));
        Assert.That(fixture.AdmissionSubscribers, Is.Zero);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_range_round_left_behind_a_head_block_that_did_not_import_is_restarted_from_the_head([Values] bool followingGossip, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer server = fixture.RangePeer("server", headSlot: fixture.Chain.Block.Message!.Slot);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        orchestrator.GossipStarted = followingGossip;
        fixture.Peers.Add(server);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);

        using CancellationTokenSource stopRounds = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task firstRound = orchestrator.FeedRangeSyncRoundAsync(stopRounds.Token);
        await ProcessUntilAsync(orchestrator, () => server.RootColumnRequests > 0, token);
        bool deferred = !fixture.Importer.IsKnown(fixture.Chain.BlockRoot);
        bool roundWaiting = await Task.WhenAny(firstRound, Task.Delay(TimeSpan.FromSeconds(1.5), token)) != firstRound;

        // Following gossip, the head falls past the gossip start distance before the retry expires; before that, only the retry age passes.
        fixture.AdvanceSlots(followingGossip ? 40UL : 2 * fixture.Chain.Spec.SlotsPerEpoch + 1);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        bool restarted = await Task.WhenAny(firstRound, Task.Delay(TimeSpan.FromSeconds(5), token)) == firstRound;

        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.AdmitPeer(custodian);
        Task secondRound = orchestrator.FeedRangeSyncRoundAsync(stopRounds.Token);
        await ProcessUntilAsync(orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);
        await stopRounds.CancelAsync();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deferred, Is.True);
        Assert.That(roundWaiting, Is.True, "the round moved past the block and waits for a peer ahead of the wall clock");
        Assert.That(restarted, Is.True, "the round in flight is ended so the next starts from the head");
        Assert.That(await BeaconSyncOrchestratorTests.EndsAsync(firstRound, token), Is.True);
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the block imports once the custodian is asked");
        Assert.That(orchestrator.SyncTip, Is.EqualTo((fixture.Chain.BlockRoot, fixture.Chain.Block.Message.Slot)));
        Assert.That(await BeaconSyncOrchestratorTests.EndsAsync(secondRound, token), Is.True);
    }

    [TestCase(BlockImportResult.DataUnavailable, false)]
    [TestCase(BlockImportResult.DataUnavailable, true)]
    [TestCase(BlockImportResult.EngineUnavailable, false)]
    [CancelAfter(30_000)]
    public async Task A_refused_block_fetches_only_missing_data_without_retaining_a_rotation_or_watch(
        BlockImportResult waiting, bool remainsUnavailable, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        Hash256 root = fixture.Chain.BlockRoot;
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token);
        if (remainsUnavailable) importer.Stuck.Add(root);
        if (waiting == BlockImportResult.EngineUnavailable) importer.EngineDown.Add(root);

        BlockImportResult result = await orchestrator.ImportAndSettleAsync(
            waiting == BlockImportResult.EngineUnavailable ? importer : fixture.Importer,
            new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity), "the set stays at its cap");
        Assert.That(custodian.RootColumnRequests, Is.EqualTo(waiting == BlockImportResult.DataUnavailable ? 1 : 0), "only missing data needs a fetch, once");
        Assert.That(result, Is.EqualTo(waiting == BlockImportResult.DataUnavailable && !remainsUnavailable ? BlockImportResult.Imported : waiting));
        Assert.That(fixture.Importer.IsKnown(root), Is.EqualTo(waiting == BlockImportResult.DataUnavailable && !remainsUnavailable));
        Assert.That(orchestrator.ColumnFetchRotationCount, Is.Zero, "a refused block keeps no rotation");
        Assert.That(fixture.SidecarPool.WatchCount, Is.Zero, "a refused block keeps no pool watch");
        if (waiting == BlockImportResult.EngineUnavailable) Assert.That(importer.ImportCalls.Count(c => c == root), Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Blocks_the_full_retry_set_refuses_cost_one_column_fetch_at_a_time(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        Gate neverOpens = new(expected: int.MaxValue);
        UnansweringPeer custodian = new("custodian", fixture.Sampled, neverOpens, TimeSpan.FromSeconds(20));
        fixture.Peers.Add(custodian);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, stop.Token);
        ForkedSignedBeaconBlock second = SecondBlobBlock(fixture);
        importer.Stuck.UnionWith([fixture.Chain.BlockRoot, second.ComputeMessageRoot()]);

        await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), stop.Token);
        await orchestrator.ImportBlockAsync(second, stop.Token);
        while (neverOpens.MaxInFlight == 0)
        {
            await Task.Delay(10, token);
        }

        await Task.Delay(200, token);
        int inFlight = orchestrator.ColumnFetchesInFlight;
        int requestsInFlight = neverOpens.MaxInFlight;
        await stop.CancelAsync();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(inFlight, Is.EqualTo(1));
        Assert.That(requestsInFlight, Is.EqualTo(1), "the second refused block asked nobody");
        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity));
    }

    [TestCase(RetrySetCapacity, TestName = "A_second_refused_block_is_asked_once_the_first_refused_fetch_ended")]
    [TestCase(RetrySetCapacity - 1, TestName = "An_ordinary_deferred_fetch_does_not_stop_a_later_refused_block_being_asked")]
    [CancelAfter(30_000)]
    public async Task A_refused_block_is_asked_after_the_previous_column_fetch_ended(int fillers, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers);
        ForkedSignedBeaconBlock first = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        ForkedSignedBeaconBlock second = SecondBlobBlock(fixture);
        importer.Stuck.UnionWith([first.ComputeMessageRoot(), second.ComputeMessageRoot()]);

        await orchestrator.ImportBlockAsync(first, token);
        await orchestrator.SettleColumnFetchesAsync(token);
        int afterFirst = custodian.RootColumnRequests;
        await orchestrator.ImportBlockAsync(second, token);
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity));
        Assert.That((afterFirst, custodian.RootColumnRequests), Is.EqualTo((1, 2)));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_refused_block_imported_meanwhile_is_not_imported_again_after_its_fetch(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token);
        Hash256 blockRoot = fixture.Chain.BlockRoot;
        importer.Stuck.Add(blockRoot);

        await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        while (orchestrator.QueuedWorkCount == 0)
        {
            await Task.Delay(10, token);
        }

        importer.Stuck.Remove(blockRoot);
        BlockImportResult otherRoute = fixture.Importer.Import(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), blockRoot, verifySignatures: true);
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(otherRoute, Is.EqualTo(BlockImportResult.Imported), "fixture: the block imports once its columns are held");
        Assert.That(importer.ImportCalls.Count(c => c == blockRoot), Is.EqualTo(1), "only the attempt the retry set refused");
    }

    private static ForkedSignedBeaconBlock SecondBlobBlock(Fixture fixture)
    {
        SignedBeaconBlock other = Test.P2P.TestChain.CreateBlock(fixture.Chain.Block.Message!.Slot + 1, fixture.Chain.AnchorRoot);
        other.Message!.Body!.BlobKzgCommitments = fixture.Chain.Block.Message.Body!.BlobKzgCommitments;
        return new ForkedSignedBeaconBlock.OfFulu(other);
    }

    private const int RetrySetCapacity = 128;

    private static async Task<(BeaconSyncOrchestrator Orchestrator, StuckBlocksImporter Importer)> CreateOrchestratorWithFullRetrySetAsync(Fixture fixture, CancellationToken token, int fillers = RetrySetCapacity, ILogManager? logs = null)
    {
        StuckBlocksImporter importer = new(fixture.Importer);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator(logs: logs);
        orchestrator.Initialize(importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.AnchorBlock), fixture.Chain.AnchorRoot);
        for (int i = 0; i < fillers; i++)
        {
            ForkedSignedBeaconBlock filler = new ForkedSignedBeaconBlock.OfFulu(Test.P2P.TestChain.CreateBlock(fixture.Chain.Block.Message!.Slot + 100 + (ulong)i, fixture.Chain.AnchorRoot));
            importer.Stuck.Add(filler.ComputeMessageRoot());
            await orchestrator.ImportBlockAsync(filler, token);
        }

        Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(fillers), "fixture: the retry set holds the fillers");
        return (orchestrator, importer);
    }

    private sealed class StuckBlocksImporter(IBlockImporter inner) : IBlockImporter
    {
        private readonly HashSet<Hash256> _accepted = [];
        private Func<Hash256, bool>? _columnsHeld;

        public HashSet<Hash256> Stuck { get; } = [];

        public HashSet<Hash256> EngineDown { get; } = [];

        public HashSet<Hash256> Invalid { get; } = [];

        /// <summary>Blocks reported imported without the inner importer, so a test can import a chain its blocks cannot.</summary>
        public HashSet<Hash256> Accepted { get; } = [];

        public List<Hash256> ImportCalls { get; } = [];

        public void AcceptOnceColumnsAreHeld(Hash256[] roots, DataColumnSidecarPool pool, ulong[] columns)
        {
            Accepted.UnionWith(roots);
            _columnsHeld = root => columns.All(column => pool.TryGet(root, column, out _));
        }

        public bool IsKnown(Hash256 blockRoot) => _accepted.Contains(blockRoot) || inner.IsKnown(blockRoot);


        public BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures)
        {
            ImportCalls.Add(blockRoot);
            bool scripted = Stuck.Contains(blockRoot) || Accepted.Contains(blockRoot) || EngineDown.Contains(blockRoot) || Invalid.Contains(blockRoot);
            if (scripted && !IsKnown(block.ParentRoot))
            {
                return BlockImportResult.UnknownParent;
            }

            return Invalid.Contains(blockRoot) ? BlockImportResult.Invalid
                : Stuck.Contains(blockRoot) || (Accepted.Contains(blockRoot) && _columnsHeld is { } columnsHeld && !columnsHeld(blockRoot)) ? BlockImportResult.DataUnavailable
                : Accepted.Contains(blockRoot) ? (_accepted.Add(blockRoot) ? BlockImportResult.Imported : BlockImportResult.AlreadyKnown)
                : EngineDown.Contains(blockRoot) ? BlockImportResult.EngineUnavailable
                : inner.Import(block, blockRoot, verifySignatures);
        }

        public ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope) => inner.ImportEnvelope(envelope);

        public bool? VerifyEnvelopeSignature(SignedExecutionPayloadEnvelope envelope) => inner.VerifyEnvelopeSignature(envelope);

        public void OnSlotTick(ulong slot) => inner.OnSlotTick(slot);

        public HeadView ComputeHead() => inner.ComputeHead();

        public void OnForkchoiceUpdated(Hash256 headRoot, Hash256 headExecutionHash, PayloadStatusV1 status) => inner.OnForkchoiceUpdated(headRoot, headExecutionHash, status);

        public void OnFinalized(CheckpointRef finalized) => inner.OnFinalized(finalized);

        public bool? OnGossipAggregate(SignedAggregateAndProof aggregate) => inner.OnGossipAggregate(aggregate);

        public bool? OnGossipAggregate(SignedAggregateAndProofGloas aggregate) => inner.OnGossipAggregate(aggregate);

        public bool? OnGossipAttesterSlashing(AttesterSlashing slashing) => inner.OnGossipAttesterSlashing(slashing);

        public bool? OnGossipAttesterSlashing(AttesterSlashingGloas slashing) => inner.OnGossipAttesterSlashing(slashing);

        public bool? OnGossipPayloadAttestation(PayloadAttestationMessage message) => inner.OnGossipPayloadAttestation(message);
    }

    private static async Task ProcessUntilAsync(BeaconSyncOrchestrator orchestrator, Func<bool> done, CancellationToken token)
    {
        while (!done())
        {
            await orchestrator.ProcessQueuedAsync(token);
            await Task.Delay(10, token);
        }
    }

    private sealed record HeldChain(BeaconSyncOrchestrator Orchestrator, StubPeer Server, List<ulong> RequestedStarts, ForkedSignedBeaconBlock[] Blocks);

    private static async Task<HeldChain> HoldChainAsync(Fixture fixture, ulong[] slots, CancellationToken token, ulong advance = 0, ILogManager? logs = null, bool processRound = true)
    {
        ForkedSignedBeaconBlock[] blocks = [new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), .. Fixture.ChainAbove(fixture.Chain.BlockRoot, slots).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        List<ulong> requestedStarts = [];
        StubPeer server = fixture.RangePeer("server", headSlot: 1000, requestedStarts, blocks);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator(logs: logs);
        fixture.Peers.Add(server);
        fixture.AdvanceSlots(advance);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        if (processRound)
        {
            await orchestrator.ProcessQueuedAsync(token);
        }

        return new HeldChain(orchestrator, server, requestedStarts, blocks);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly BeaconChainStore _store = new(new MemColumnsDb<BeaconChainDbColumns>());
        private BeaconDiscovery _discovery = null!;
        private ManualTimestamper _time = null!;
        private MutablePool _pool = null!;

        public BeaconChainStore Store => _store;
        public BeaconDiscovery Discovery => _discovery;

        public ImportableBlobBlock Chain { get; } = ImportableBlobBlock.Create();
        public DataColumnSidecarPool SidecarPool { get; } = new();

        /// <summary>Starts at epoch 1, which keeps the epoch-0 block inside the data availability window.</summary>
        public SlotClock Clock { get; private set; } = null!;

        public IBlockImporter Importer { get; private set; } = null!;

        public List<IBeaconSyncPeer> Peers { get; } = [];

        public ulong[] Sampled { get; private set; } = [];

        public ulong[] Unsampled => [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Except(Sampled)];

        public IEngineDriver Engine { get; private set; } = null!;

        public static Fixture Create(IEngineDriver? engine = null, byte[]? identity = null)
        {
            Fixture fixture = new() { Engine = engine ?? new NoOpEngineDriver() };
            if (identity is not null)
            {
                fixture._store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, identity);
            }
            fixture._discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, fixture.Chain.Spec, fixture._store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            fixture._discovery.CreateDiscv5Services(IPAddress.Loopback);
            fixture.Sampled = [.. new DiscoveryNodeCustodySource(fixture._discovery).Current!.SampledColumns];
            fixture._time = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(fixture.Chain.Spec.GenesisTime + fixture.Chain.Spec.SlotsPerEpoch * fixture.Chain.Spec.SecondsPerSlot)).UtcDateTime);
            fixture.Clock = new SlotClock(fixture.Chain.Spec, fixture._time);
            fixture.Importer = new BlockImporterFactory(fixture.Chain.Spec, fixture._store, fixture.Chain.Pubkeys, fixture.Engine, new BeaconChainConfig(), LimboLogs.Instance, fixture.SidecarPool, fixture.Clock, fixture._discovery)
                .Create(new ForkedBeaconState.OfFulu(fixture.Chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.AnchorBlock), fixture.Chain.AnchorRoot);
            return fixture;
        }

        public void AdvanceSlots(ulong slots) => _time.Add(TimeSpan.FromSeconds(slots * Chain.Spec.SecondsPerSlot));

        public void GiveColumn(ulong column) => SidecarPool.Add(Chain.BlockRoot, Chain.Block.Message!.Slot, Chain.Columns[(int)column]);

        public StubPeer Peer(string id, ulong[] custodied)
        {
            PeerColumnCustody custody = new(custodied, isAdvertised: true);
            return new StubPeer(
                id,
                Chain.Block.Message!.Slot,
                static (_, _) => [],
                custody: custody,
                rootHandler: identifiers => [.. identifiers.Single().Columns!.Where(custody.Custodies).Select(c => Chain.Columns[(int)c])]);
        }

        public StubPeer SilentPeer(string id, ulong[] custodied) =>
            new(id, Chain.Block.Message!.Slot, static (_, _) => [], custody: new PeerColumnCustody(custodied, isAdvertised: true), rootHandler: static _ => []);

        public StubPeer RangePeer(string id, ulong headSlot, List<ulong>? requestStarts = null, ForkedSignedBeaconBlock[]? blocks = null)
        {
            ForkedSignedBeaconBlock[] served = blocks ?? [new ForkedSignedBeaconBlock.OfFulu(Chain.Block)];
            return new StubPeer(id, headSlot, (start, count) =>
                {
                    requestStarts?.Add(start);
                    return [.. served.Where(b => b.Slot >= start && b.Slot < start + count)];
                },
                custody: new PeerColumnCustody(Sampled, isAdvertised: true), rootHandler: static _ => []);
        }

        public static SignedBeaconBlock[] ChainAbove(Hash256 root, ulong[] slots)
        {
            List<SignedBeaconBlock> chain = [];
            Hash256 parentRoot = root;
            foreach (ulong slot in slots)
            {
                SignedBeaconBlock block = Test.P2P.TestChain.CreateBlock(slot, parentRoot);
                chain.Add(block);
                parentRoot = new ForkedSignedBeaconBlock.OfFulu(block).ComputeMessageRoot();
            }

            return [.. chain];
        }

        public void AdmitPeer(IBeaconSyncPeer peer)
        {
            Peers.Add(peer);
            _pool.Admit(peer);
        }

        public int AdmissionSubscribers => _pool.Subscribers;

        public BeaconSyncOrchestrator CreateOrchestrator(bool routeAdmissions = true, ILogManager? logs = null)
        {
            MutablePool pool = _pool = new(Peers);
            BeaconSyncOrchestrator orchestrator = new(
                new BeaconChainConfig(),
                Chain.Spec,
                _store,
                new BlockImporterFactory(Chain.Spec, _store, Chain.Pubkeys, Engine, new BeaconChainConfig(), LimboLogs.Instance, SidecarPool, Clock, _discovery),
                Engine,
                pool,
                new RangeSync(pool, LimboLogs.Instance, SidecarPool, Chain.Spec, Clock, _discovery),
                Clock,
                new GossipRouter(Chain.Spec, Clock, LimboLogs.Instance),
                new BeaconChainStatusHolder(Chain.Spec, Timestamper.Default),
                logs ?? LimboLogs.Instance,
                discovery: _discovery,
                columnPool: SidecarPool)
            {
                GossipStarted = true,
            };
            orchestrator.Initialize(Importer, new ForkedSignedBeaconBlock.OfFulu(Chain.AnchorBlock), Chain.AnchorRoot);
            if (routeAdmissions)
            {
                orchestrator.RoutePeerAdmissions();
            }

            return orchestrator;
        }

        public ValueTask DisposeAsync() => _discovery.DisposeAsync();
    }

    private sealed class MutablePool(List<IBeaconSyncPeer> peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) => [.. peers.Where(p => p.HeadSlot >= minHeadSlot)];

        public event Action<IBeaconSyncPeer>? PeerAdmitted;

        public int Subscribers => PeerAdmitted?.GetInvocationList().Length ?? 0;

        public void Admit(IBeaconSyncPeer peer) => PeerAdmitted?.Invoke(peer);
    }

    internal sealed class Gate(int expected)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _maxInFlight;
        private readonly object _arrivalLock = new();
        private TaskCompletionSource _arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public void Open() => _open.TrySetResult();

        public async Task WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            int inFlight = Interlocked.Increment(ref _inFlight);
            lock (_arrivalLock)
            {
                InterlockedMax(ref _maxInFlight, inFlight);
                _arrival.TrySetResult();
                _arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (inFlight >= expected)
            {
                _open.TrySetResult();
            }

            try
            {
                await _open.Task.WaitAsync(timeout, token);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public async Task WaitForRequestsAsync(int count, CancellationToken token)
        {
            while (true)
            {
                Task arrival;
                lock (_arrivalLock)
                {
                    if (MaxInFlight >= count)
                    {
                        return;
                    }

                    arrival = _arrival.Task;
                }

                await arrival.WaitAsync(TimeSpan.FromSeconds(30), token);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current = Volatile.Read(ref target);
            while (value > current && Interlocked.CompareExchange(ref target, value, current) != current)
            {
                current = Volatile.Read(ref target);
            }
        }
    }

    /// <summary>Unconfigured requests fail; each scenario must explicitly provide every request it needs.</summary>
    internal abstract class TestPeer(string id, ulong headSlot, PeerColumnCustody? custody = null) : IBeaconSyncPeer
    {
        public string Id => id;
        public ulong HeadSlot => headSlot;
        public virtual PeerColumnCustody Custody { get; } = custody ?? StubPeer.AllColumns;

        public virtual Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) => throw new NotSupportedException();

        public virtual Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) => throw new NotSupportedException();

        public virtual Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public virtual Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) => throw new NotSupportedException();

        public virtual Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public virtual Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) => throw new NotSupportedException();

        public virtual Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token) => throw new NotSupportedException();

        public virtual Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token) => throw new NotSupportedException();

        public virtual void ReportFailure(PeerFailureReason reason, string? detail = null) { }
    }

    /// <summary>Wait for all custodians before failing requests to witness concurrent fan-out.</summary>
    internal sealed class UnansweringPeer(string id, ulong[] custodied, Gate gate, TimeSpan requestTimeout) : TestPeer(id, ulong.MaxValue, new(custodied, isAdvertised: true))
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public override async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
        {
            await gate.WaitAsync(requestTimeout, token);
            throw new TimeoutException($"{Id} did not answer");
        }

        public override void ReportFailure(PeerFailureReason reason, string? detail = null) => Interlocked.Increment(ref _failures);

        public override async Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
        {
            await gate.WaitAsync(requestTimeout, token);
            throw new TimeoutException($"{Id} did not answer");
        }
    }
}
