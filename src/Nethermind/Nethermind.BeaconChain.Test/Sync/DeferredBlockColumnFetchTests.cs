// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
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
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// fulu/fork-choice.md <c>is_data_available</c>: a block deferred for its sampled columns imports only once all of them
/// are held, and fulu/p2p-interface.md peers serve only the columns they custody, so repeated fetches must reach every
/// custodian the pool has, including one that connected after the block was deferred, while one import costs a bounded
/// number of requests however many custodians are connected.
/// </summary>
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That((withoutCustodian, sameCustodians), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
            Assert.That(custodianConnected, Is.EqualTo(BlockImportResult.Imported), "the newly connected custodian is asked without waiting for the next slot");
            Assert.That(custodian.RootColumnRequests, Is.EqualTo(1));
            Assert.That(bystander.RootColumnRequests, Is.Zero, "a peer custodying no sampled column is never asked");
        }
    }

    /// <summary>fulu/p2p-interface.md DataColumnSidecarsByRoot names the block by root, so a custodian whose last status head is behind a gossip block at the head is still asked.</summary>
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockSlot, Is.GreaterThan(behind.HeadSlot));
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
            Assert.That(behind.RootColumnRequests, Is.EqualTo(1));
        }
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
        // The same peer after its MetaData raised its custody group count.
        fixture.Peers[0] = grown;
        BlockImportResult custodyGrown = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(custodyingOnlyHeld, Is.EqualTo(BlockImportResult.DataUnavailable));
            Assert.That(custodyGrown, Is.EqualTo(BlockImportResult.Imported), "a peer that custodied only held columns was never asked, so it is asked once it custodies a missing one");
            Assert.That(grown.RootColumnRequests, Is.EqualTo(1));
        }
    }

    /// <summary>The custodians of one import are asked at once for the same columns, so a copy of a column an earlier reply supplied is neither verified again nor held against its peer.</summary>
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

    [Test]
    [CancelAfter(30_000)]
    public async Task A_sample_spread_over_single_column_custodians_is_retrieved_a_bounded_number_of_custodians_per_attempt(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer[] custodians = [.. fixture.Sampled.Select(c => fixture.Peer($"custodian-{c}", [c]))];
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.AddRange(custodians);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        int attemptsNeeded = (custodians.Length + CustodiansPerImport - 1) / CustodiansPerImport;

        List<BlockImportResult> results = [];
        List<int> requestsPerAttempt = [];
        for (int attempt = 0; attempt < attemptsNeeded; attempt++)
        {
            int before = custodians.Sum(static p => p.RootColumnRequests);
            results.Add(await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token));
            requestsPerAttempt.Add(custodians.Sum(static p => p.RootColumnRequests) - before);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(custodians, Has.Length.GreaterThan(CustodiansPerImport), "more single-column custodians than one import asks");
            Assert.That(results[^1], Is.EqualTo(BlockImportResult.Imported), "the rotation reaches every custodian within the same slot");
            Assert.That(results[..^1], Is.All.EqualTo(BlockImportResult.DataUnavailable));
            Assert.That(requestsPerAttempt, Is.All.LessThanOrEqualTo(CustodiansPerImport));
            Assert.That(custodians.Select(static p => p.RootColumnRequests), Is.All.EqualTo(1), "each custodian is asked once");
        }
    }

    /// <summary>
    /// Peers answering by root with no sidecars must neither hide a serving custodian nor multiply the cost of one import:
    /// the rotation asks a bounded number per import, reaches a custodian present from the start within <c>ceil(N / 3)</c>
    /// imports, and one that connects later on the next import.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Silent_custodians_cost_a_bounded_number_of_requests_per_import_and_do_not_hide_a_serving_custodian(
        [Values(1, 4, 10, 40)] int silentCount,
        [Values] bool connectsAfterDeferral,
        CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer[] silent = [.. Enumerable.Range(0, silentCount).Select(i => fixture.SilentPeer($"silent-{i}", fixture.Sampled))];
        StubPeer honest = fixture.Peer("honest", fixture.Sampled);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.AddRange(silent);
        if (!connectsAfterDeferral)
        {
            fixture.Peers.Add(honest);
        }

        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        int custodianCount = silentCount + 1;
        int maxAttempts = (custodianCount + CustodiansPerImport - 1) / CustodiansPerImport;
        List<int> requestsPerAttempt = [];
        int attempts = 0;
        BlockImportResult result = BlockImportResult.DataUnavailable;
        while (result != BlockImportResult.Imported && attempts < custodianCount)
        {
            if (connectsAfterDeferral && attempts == 1)
            {
                fixture.Peers.Add(honest);
            }

            int before = silent.Sum(static p => p.RootColumnRequests) + honest.RootColumnRequests;
            result = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
            requestsPerAttempt.Add(silent.Sum(static p => p.RootColumnRequests) + honest.RootColumnRequests - before);
            attempts++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the pool holds a custodian serving every sampled column");
            Assert.That(requestsPerAttempt, Is.All.LessThanOrEqualTo(CustodiansPerImport), "one import asks a bounded number of custodians however many are connected");
            Assert.That(attempts, connectsAfterDeferral ? Is.EqualTo(2) : Is.LessThanOrEqualTo(maxAttempts),
                connectsAfterDeferral ? "a custodian that connected since the last import is asked first" : "the rotation reaches every custodian within ceil(N / 3) imports");
            Assert.That(honest.RootColumnRequests, Is.EqualTo(1));
            Assert.That(silent.Select(static p => p.RootColumnRequests), Is.All.LessThanOrEqualTo(1), "a peer is asked once per slot for a block");
        }
    }

    /// <summary>
    /// New peer ids are cheap, so custodians presenting fresh ones before every import must not take every place of each import:
    /// a custodian seen at the previous import keeps one, and is asked on the next import.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Custodians_churning_their_peer_ids_do_not_keep_an_earlier_custodian_from_being_asked(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer honest = fixture.Peer("honest", fixture.Sampled);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        const int MaxImports = 10;
        List<StubPeer> churned = [];
        List<int> requestsPerImport = [];
        BlockImportResult result = BlockImportResult.DataUnavailable;
        while (result != BlockImportResult.Imported && requestsPerImport.Count < MaxImports)
        {
            StubPeer[] fresh = [.. Enumerable.Range(0, CustodiansPerImport).Select(i => fixture.SilentPeer($"churned-{churned.Count + i}", fixture.Sampled))];
            churned.AddRange(fresh);
            fixture.Peers.Clear();
            fixture.Peers.AddRange(fresh);
            // Last, so the pool's own order would put it behind every newcomer.
            fixture.Peers.Add(honest);
            int before = churned.Sum(static p => p.RootColumnRequests) + honest.RootColumnRequests;
            result = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
            requestsPerImport.Add(churned.Sum(static p => p.RootColumnRequests) + honest.RootColumnRequests - before);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
            Assert.That(requestsPerImport, Has.Count.EqualTo(2), "the custodian seen at the first import is asked at the second");
            Assert.That(requestsPerImport, Is.All.LessThanOrEqualTo(CustodiansPerImport));
            Assert.That(honest.RootColumnRequests, Is.EqualTo(1));
        }
    }

    /// <summary>Peers that disconnected are forgotten, so churning peer ids cannot grow a block's rotation.</summary>
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That((silentReply, sameSlot), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
            Assert.That(requestsInFirstSlot, Is.EqualTo(1), "an asked peer is not asked again for the same block in the same slot");
            Assert.That(custodian.RootColumnRequests, Is.EqualTo(2), "the next slot asks it again");
            Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the retried block imports once its columns arrive");
        }
    }

    /// <summary>A block's fetch rotation lives only while the block waits for a retry, so the rotations stay bounded by the retry set.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_blocks_fetch_rotation_is_forgotten_once_it_imports_or_its_retry_expires([Values] bool imports, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(fixture.SilentPeer("silent", fixture.Sampled));
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);

        await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        int whileDeferred = orchestrator.ColumnFetchRotationCount;
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileDeferred, Is.EqualTo(1));
            Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.EqualTo(imports));
            Assert.That(orchestrator.ColumnFetchRotationCount, Is.Zero);
        }
    }

    /// <summary>The by-root requests of one import run together, so custodians that never answer cost one request timeout between them, not one each.</summary>
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.DataUnavailable));
            Assert.That(allAsked.MaxInFlight, Is.EqualTo(CustodiansPerImport), "every custodian of the import is asked before any answers");
            Assert.That(elapsed.Elapsed, Is.LessThan(requestTimeout), "no request waited out its timeout behind another");
            Assert.That(custodians.Select(static p => p.Failures), Is.All.EqualTo(1), "each failed request penalizes its peer");
        }
    }

    /// <summary>
    /// A head block stuck on its columns costs each range-sync round one refetch of the same batches and at most one by-root
    /// fetch per slot, and imports in the first round after a custodian of its columns joins.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_stuck_head_block_costs_a_bounded_refetch_per_round_and_imports_once_a_custodian_joins(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer server = fixture.RangePeer("server", headSlot: fixture.Clock.CurrentSlot);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        fixture.Peers.Add(server);
        const int StuckRounds = 3;

        List<int> batchesPerRound = [];
        for (int round = 0; round < StuckRounds; round++)
        {
            int before = server.Requests;
            await orchestrator.FeedRangeSyncRoundAsync(token);
            await orchestrator.ProcessQueuedAsync(token);
            batchesPerRound.Add(server.Requests - before);
        }

        bool stuck = !fixture.Importer.IsKnown(fixture.Chain.BlockRoot);
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        await orchestrator.FeedRangeSyncRoundAsync(token);
        await orchestrator.ProcessQueuedAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stuck, Is.True, "no peer serves the block's columns");
            Assert.That(batchesPerRound, Is.All.EqualTo(batchesPerRound[0]), "each round refetches the same batches, no more");
            Assert.That(server.RootColumnRequests, Is.EqualTo(1), "re-delivered copies of the block do not ask the same custodian again within the slot");
            Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the round after a custodian joins imports the block");
            Assert.That(orchestrator.SyncTip, Is.EqualTo((fixture.Chain.BlockRoot, fixture.Chain.Block.Message!.Slot)));
        }
    }

    /// <summary>
    /// A round follows the wall clock past a head block that did not import, so without a restart the block is never fetched
    /// again. Following gossip, a head more than two epochs behind restarts it; before that, the block's retry expiring does.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_range_round_left_behind_a_head_block_that_did_not_import_is_restarted_from_the_head([Values] bool followingGossip, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        // Its head is the block, so after the first batch no peer is ahead and the round waits for one.
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
        fixture.Peers.Add(custodian);
        Task secondRound = orchestrator.FeedRangeSyncRoundAsync(stopRounds.Token);
        await ProcessUntilAsync(orchestrator, () => fixture.Importer.IsKnown(fixture.Chain.BlockRoot), token);
        await stopRounds.CancelAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deferred, Is.True);
            Assert.That(roundWaiting, Is.True, "the round moved past the block and waits for a peer ahead of the wall clock");
            Assert.That(restarted, Is.True, "the round in flight is ended so the next starts from the head");
            Assert.That(await BeaconSyncOrchestratorTests.EndsAsync(firstRound, token), Is.True);
            Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the restarted round delivers the block again and it imports");
            Assert.That(orchestrator.SyncTip, Is.EqualTo((fixture.Chain.BlockRoot, fixture.Chain.Block.Message.Slot)));
            Assert.That(await BeaconSyncOrchestratorTests.EndsAsync(secondRound, token), Is.True);
        }
    }

    /// <summary>
    /// A deferred block the full retry set refuses still gets its by-root column fetch and one import attempt from its result, so it
    /// imports without waiting for range sync; that attempt does not fetch again, and the set stays at its cap.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_deferred_block_the_full_retry_set_refuses_still_fetches_its_columns_and_imports_once(
        [Values] bool importerStaysUnavailable,
        CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        Hash256 blockRoot = fixture.Chain.BlockRoot;
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token);

        if (importerStaysUnavailable)
        {
            importer.Stuck.Add(blockRoot);
        }

        BlockImportResult refused = await orchestrator.ImportAndSettleAsync(fixture.Importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity), "the set stays at its cap");
            Assert.That(custodian.RootColumnRequests, Is.EqualTo(1), "the refused block's columns are fetched once, and the import attempt after them does not fetch again");
            Assert.That(refused, Is.EqualTo(importerStaysUnavailable ? BlockImportResult.DataUnavailable : BlockImportResult.Imported));
            Assert.That(fixture.Importer.IsKnown(blockRoot), Is.EqualTo(!importerStaysUnavailable));
            Assert.That(orchestrator.ColumnFetchRotationCount, Is.Zero, "a refused block keeps no rotation");
            Assert.That(fixture.SidecarPool.WatchCount, Is.Zero, "a refused block keeps no pool watch");
        }
    }

    /// <summary>The refused blocks are not tracked, so blocks the full retry set refuses cost one by-root fetch at a time, however many are sent.</summary>
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inFlight, Is.EqualTo(1));
            Assert.That(requestsInFlight, Is.EqualTo(1), "the second refused block asked nobody");
            Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity));
        }
    }

    /// <summary>Each refused block's fetch ends and frees the one slot, so the refused block after it is asked too.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_second_refused_block_is_asked_once_the_first_refused_fetch_ended(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token);
        ForkedSignedBeaconBlock first = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        ForkedSignedBeaconBlock second = SecondBlobBlock(fixture);
        importer.Stuck.UnionWith([first.ComputeMessageRoot(), second.ComputeMessageRoot()]);

        await orchestrator.ImportBlockAsync(first, token);
        await orchestrator.SettleColumnFetchesAsync(token);
        int afterFirst = custodian.RootColumnRequests;
        await orchestrator.ImportBlockAsync(second, token);
        await orchestrator.SettleColumnFetchesAsync(token);

        Assert.That((afterFirst, custodian.RootColumnRequests), Is.EqualTo((1, 2)));
    }

    /// <summary>An ordinary deferred fetch does not take the refused-fetch slot, so a block the set later refuses is still asked.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task An_ordinary_deferred_fetch_does_not_stop_a_later_refused_block_being_asked(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token, fillers: RetrySetCapacity - 1);
        ForkedSignedBeaconBlock ordinary = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        ForkedSignedBeaconBlock refused = SecondBlobBlock(fixture);
        importer.Stuck.UnionWith([ordinary.ComputeMessageRoot(), refused.ComputeMessageRoot()]);

        await orchestrator.ImportBlockAsync(ordinary, token);
        await orchestrator.SettleColumnFetchesAsync(token);
        int afterOrdinary = custodian.RootColumnRequests;
        await orchestrator.ImportBlockAsync(refused, token);
        await orchestrator.SettleColumnFetchesAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(orchestrator.PendingRetryBlockCount, Is.EqualTo(RetrySetCapacity));
            Assert.That((afterOrdinary, custodian.RootColumnRequests), Is.EqualTo((1, 2)));
        }
    }

    /// <summary>A refused block that another route imported while its fetch ran is not run through the importer again.</summary>
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherRoute, Is.EqualTo(BlockImportResult.Imported), "fixture: the block imports once its columns are held");
            Assert.That(importer.ImportCalls.Count(c => c == blockRoot), Is.EqualTo(1), "only the attempt the retry set refused");
        }
    }

    /// <summary>A block deferred for the engine, not for columns, has nothing to fetch even when the full retry set refuses it.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task An_engine_unavailable_block_the_full_retry_set_refuses_asks_no_custodian(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        StubPeer custodian = fixture.Peer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        (BeaconSyncOrchestrator orchestrator, StuckBlocksImporter importer) = await CreateOrchestratorWithFullRetrySetAsync(fixture, token);
        importer.EngineDown.Add(fixture.Chain.BlockRoot);

        BlockImportResult result = await orchestrator.ImportAndSettleAsync(importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.EngineUnavailable));
            Assert.That(custodian.RootColumnRequests, Is.Zero);
            Assert.That(importer.ImportCalls.Count(c => c == fixture.Chain.BlockRoot), Is.EqualTo(1));
        }
    }

    private static ForkedSignedBeaconBlock SecondBlobBlock(Fixture fixture)
    {
        SignedBeaconBlock other = Test.P2P.TestChain.CreateBlock(fixture.Chain.Block.Message!.Slot + 1, fixture.Chain.AnchorRoot);
        other.Message!.Body!.BlobKzgCommitments = fixture.Chain.Block.Message.Body!.BlobKzgCommitments;
        return new ForkedSignedBeaconBlock.OfFulu(other);
    }

    private const int RetrySetCapacity = 128;

    /// <summary>An orchestrator over the real importer wrapped so that <paramref name="fixture"/>'s retry set holds <paramref name="fillers"/> blocks.</summary>
    private static async Task<(BeaconSyncOrchestrator Orchestrator, StuckBlocksImporter Importer)> CreateOrchestratorWithFullRetrySetAsync(Fixture fixture, CancellationToken token, int fillers = RetrySetCapacity)
    {
        StuckBlocksImporter importer = new(fixture.Importer);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
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

    /// <summary>Answers <see cref="BlockImportResult.DataUnavailable"/> for the blocks in <see cref="Stuck"/>, <see cref="BlockImportResult.EngineUnavailable"/> for those in <see cref="EngineDown"/>, and defers to <paramref name="inner"/> for the rest.</summary>
    private sealed class StuckBlocksImporter(IBlockImporter inner) : IBlockImporter
    {
        public HashSet<Hash256> Stuck { get; } = [];

        public HashSet<Hash256> EngineDown { get; } = [];

        public List<Hash256> ImportCalls { get; } = [];

        public bool IsKnown(Hash256 blockRoot) => inner.IsKnown(blockRoot);

        public bool IsExpectedProposer(ForkedSignedBeaconBlock block) => inner.IsExpectedProposer(block);

        public BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures)
        {
            ImportCalls.Add(blockRoot);
            return Stuck.Contains(blockRoot) ? BlockImportResult.DataUnavailable
                : EngineDown.Contains(blockRoot) ? BlockImportResult.EngineUnavailable
                : inner.Import(block, blockRoot, verifySignatures);
        }

        public ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope) => inner.ImportEnvelope(envelope);

        public void OnSlotTick(ulong slot) => inner.OnSlotTick(slot);

        public HeadView ComputeHead() => inner.ComputeHead();

        public void OnInvalidExecutionPayload(Hash256 blockRoot, Hash256? latestValidHash) => inner.OnInvalidExecutionPayload(blockRoot, latestValidHash);

        public void OnFinalized(CheckpointRef finalized) => inner.OnFinalized(finalized);

        public void OnGossipAggregate(SignedAggregateAndProof aggregate) => inner.OnGossipAggregate(aggregate);

        public void OnGossipAggregate(SignedAggregateAndProofGloas aggregate) => inner.OnGossipAggregate(aggregate);

        public bool OnGossipAttesterSlashing(AttesterSlashing slashing) => inner.OnGossipAttesterSlashing(slashing);

        public bool OnGossipAttesterSlashing(AttesterSlashingGloas slashing) => inner.OnGossipAttesterSlashing(slashing);

        public bool OnGossipPayloadAttestation(PayloadAttestationMessage message) => inner.OnGossipPayloadAttestation(message);
    }

    /// <summary>Runs the queued work on this thread, as the worker would, until <paramref name="done"/> holds.</summary>
    private static async Task ProcessUntilAsync(BeaconSyncOrchestrator orchestrator, Func<bool> done, CancellationToken token)
    {
        while (!done())
        {
            await orchestrator.ProcessQueuedAsync(token);
            await Task.Delay(10, token);
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly BeaconChainStore _store = new(new MemColumnsDb<BeaconChainDbColumns>());
        private BeaconDiscovery _discovery = null!;
        private ManualTimestamper _time = null!;

        public ImportableBlobBlock Chain { get; } = ImportableBlobBlock.Create();
        public DataColumnSidecarPool SidecarPool { get; } = new();

        /// <summary>Starts at epoch 1, which keeps the epoch-0 block inside the data availability window.</summary>
        public SlotClock Clock { get; private set; } = null!;

        public IBlockImporter Importer { get; private set; } = null!;

        /// <summary>The connected peers, which a test changes between imports.</summary>
        public List<IBeaconSyncPeer> Peers { get; } = [];

        /// <summary>This node's sampled columns, which depend on its randomly generated identity.</summary>
        public ulong[] Sampled { get; private set; } = [];

        public ulong[] Unsampled => [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Except(Sampled)];

        public static Fixture Create()
        {
            Fixture fixture = new();
            fixture._discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, fixture.Chain.Spec, fixture._store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            // Resolves the identity and local custody exactly as Start does, without binding a socket.
            fixture._discovery.CreateDiscv5Services(IPAddress.Loopback);
            fixture.Sampled = [.. new DiscoveryNodeCustodySource(fixture._discovery).Current!.SampledColumns];
            fixture._time = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(fixture.Chain.Spec.GenesisTime + fixture.Chain.Spec.SlotsPerEpoch * fixture.Chain.Spec.SecondsPerSlot)).UtcDateTime);
            fixture.Clock = new SlotClock(fixture.Chain.Spec, fixture._time);
            fixture.Importer = new BlockImporterFactory(fixture.Chain.Spec, fixture._store, fixture.Chain.Pubkeys, new NoOpEngineDriver(), new BeaconChainConfig(), LimboLogs.Instance, fixture.SidecarPool, fixture.Clock, fixture._discovery)
                .Create(new ForkedBeaconState.OfFulu(fixture.Chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.AnchorBlock), fixture.Chain.AnchorRoot);
            return fixture;
        }

        public void AdvanceSlots(ulong slots) => _time.Add(TimeSpan.FromSeconds(slots * Chain.Spec.SecondsPerSlot));

        /// <summary>Gives the pool the chain block's sidecar of <paramref name="column"/>, as gossip does.</summary>
        public void GiveColumn(ulong column) => SidecarPool.Add(Chain.BlockRoot, Chain.Block.Message!.Slot, Chain.Columns[(int)column]);

        /// <summary>An honest peer serving by root only the asked columns it custodies.</summary>
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

        /// <summary>A peer that advertises <paramref name="custodied"/> but answers every by-root request with no sidecars.</summary>
        public StubPeer SilentPeer(string id, ulong[] custodied) =>
            new(id, Chain.Block.Message!.Slot, static (_, _) => [], custody: new PeerColumnCustody(custodied, isAdvertised: true), rootHandler: static _ => []);

        /// <summary>A peer serving the block by range and custodying every sampled column, but serving no column.</summary>
        public StubPeer RangePeer(string id, ulong headSlot)
        {
            ulong blockSlot = Chain.Block.Message!.Slot;
            ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(Chain.Block);
            return new StubPeer(id, headSlot, (start, count) => blockSlot >= start && blockSlot < start + count ? [block] : [],
                custody: new PeerColumnCustody(Sampled, isAdvertised: true), rootHandler: static _ => []);
        }

        public BeaconSyncOrchestrator CreateOrchestrator()
        {
            MutablePool pool = new(Peers);
            BeaconSyncOrchestrator orchestrator = new(
                new BeaconChainConfig(),
                Chain.Spec,
                _store,
                new BlockImporterFactory(Chain.Spec, _store, Chain.Pubkeys, new NoOpEngineDriver(), new BeaconChainConfig(), LimboLogs.Instance, SidecarPool, Clock, _discovery),
                new NoOpEngineDriver(),
                pool,
                new RangeSync(pool, LimboLogs.Instance, SidecarPool, Chain.Spec, Clock, _discovery),
                Clock,
                new GossipRouter(Chain.Spec, Clock, LimboLogs.Instance),
                new BeaconChainStatusHolder(Chain.Spec, Timestamper.Default),
                LimboLogs.Instance,
                discovery: _discovery,
                columnPool: SidecarPool)
            {
                // Gossip needs a started libp2p host, which is not what these tests are about.
                GossipStarted = true,
            };
            orchestrator.Initialize(Importer, new ForkedSignedBeaconBlock.OfFulu(Chain.AnchorBlock), Chain.AnchorRoot);
            return orchestrator;
        }

        public ValueTask DisposeAsync() => _discovery.DisposeAsync();
    }

    private sealed class MutablePool(List<IBeaconSyncPeer> peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) => [.. peers.Where(p => p.HeadSlot >= minHeadSlot)];
    }

    /// <summary>Opens once <paramref name="expected"/> requests are in flight together, recording the most ever in flight.</summary>
    internal sealed class Gate(int expected)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _maxInFlight;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            int inFlight = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, inFlight);
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

        private static void InterlockedMax(ref int target, int value)
        {
            int current = Volatile.Read(ref target);
            while (value > current && Interlocked.CompareExchange(ref target, value, current) != current)
            {
                current = Volatile.Read(ref target);
            }
        }
    }

    /// <summary>A custodian whose by-root requests answer nothing until every custodian of the import was asked, then fails as a timed-out request would.</summary>
    internal sealed class UnansweringPeer(string id, ulong[] custodied, Gate gate, TimeSpan requestTimeout) : IBeaconSyncPeer
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public string Id => id;

        public ulong HeadSlot => ulong.MaxValue;

        public PeerColumnCustody Custody { get; } = new(custodied, isAdvertised: true);

        public async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
        {
            await gate.WaitAsync(requestTimeout, token);
            throw new TimeoutException($"{id} did not answer");
        }

        public void ReportFailure(PeerFailureReason reason, string? detail = null) => Interlocked.Increment(ref _failures);

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public async Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
        {
            await gate.WaitAsync(requestTimeout, token);
            throw new TimeoutException($"{id} did not answer");
        }

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token) => throw new NotSupportedException();
    }
}
