// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;
using static Nethermind.BeaconChain.Test.Sync.DeferredBlockColumnFetchTests;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// fulu/fork-choice.md <c>is_data_available</c>: a head block deferred for its sampled columns must import as soon as they are held,
/// gossiped or fetched by root, not on the next slot tick, or every blob-carrying head block lands a slot late. The fetch and the pool's
/// completion signal reach the worker only as work items, since fork choice and the public-key cache are read on that loop alone.
/// </summary>
public class ColumnArrivalRetryTests
{
    [Test]
    [CancelAfter(30_000)]
    public async Task A_deferred_block_imports_when_gossip_completes_its_columns_without_waiting_for_a_tick(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        BlockImportResult deferred = await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        ulong slotAtDeferral = fixture.Clock.CurrentSlot;

        foreach (ulong column in fixture.Sampled[..^1])
        {
            fixture.GiveColumn(column);
        }

        int queuedWithOneMissing = orchestrator.QueuedWorkCount;
        fixture.GiveColumn(fixture.Sampled[^1]);
        int queuedOnceComplete = orchestrator.QueuedWorkCount;
        bool importedOffTheWorker = fixture.Importer.IsKnown(fixture.Chain.BlockRoot);
        await orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deferred, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(queuedWithOneMissing, Is.Zero, "a column short of the sample wakes nothing");
        Assert.That(queuedOnceComplete, Is.EqualTo(1), "the last column queues the retry");
        Assert.That(importedOffTheWorker, Is.False, "the pool only queues work; the worker is the one that imports");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "imported before any slot tick");
        Assert.That(fixture.Clock.CurrentSlot, Is.EqualTo(slotAtDeferral));
        Assert.That(fixture.SidecarPool.WatchCount, Is.Zero, "the watch ends with the wait");
    }

    /// <summary>A flood of cheap sidecars must not add work: only a root the worker waits on queues anything, and it queues once.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Sidecars_of_a_root_nobody_waits_on_queue_nothing_and_repeats_of_an_awaited_column_queue_once(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        await orchestrator.ImportAndSettleAsync(fixture.Importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        ulong slot = fixture.Chain.Block.Message!.Slot;

        foreach (ulong column in fixture.Sampled)
        {
            fixture.SidecarPool.Add(TestItem.KeccakA, slot, fixture.Chain.Columns[(int)column]);
        }

        int queuedForUnknownRoot = orchestrator.QueuedWorkCount;
        foreach (ulong column in fixture.Sampled)
        {
            fixture.GiveColumn(column);
            fixture.GiveColumn(column);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(queuedForUnknownRoot, Is.Zero);
        Assert.That(orchestrator.QueuedWorkCount, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_deferral_fetches_by_root_only_the_missing_columns_and_asks_nobody_again_in_the_same_slot(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        fixture.GiveColumn(fixture.Sampled[0]);
        StubPeer custodian = fixture.SilentPeer("custodian", fixture.Sampled);
        fixture.Peers.Add(custodian);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);

        await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);
        int requestsAfterDeferral = custodian.RootColumnRequests;
        ulong[][] requested = [.. custodian.RequestedColumns];
        await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(requestsAfterDeferral, Is.EqualTo(1), "the deferral itself starts the fetch");
        Assert.That(requested, Has.Length.EqualTo(1));
        Assert.That(requested[0], Is.EqualTo(fixture.Sampled[1..]), "the held column is not asked for");
        Assert.That(custodian.RootColumnRequests, Is.EqualTo(1), "a second deferral in the same slot asks the same custodian nothing");
    }

    /// <summary>The deferral returns while the custodian's answer is pending, a second deferral does not start a second fetch, and the answer reaches the importer only through the worker.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_by_root_fetch_runs_off_the_worker_once_per_block_and_its_columns_import_the_block_through_the_queue(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        AsyncRootPeer slow = new("slow", fixture.Sampled, fixture.Chain.Block.Message!.Slot);
        fixture.Peers.Add(slow);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);

        BlockImportResult first = await orchestrator.ImportBlockAsync(block, token);
        int inFlightWhileAsked = orchestrator.ColumnFetchesInFlight;
        // Would be asked first by the rotation, were a second fetch allowed to start.
        StubPeer newcomer = fixture.SilentPeer("newcomer", fixture.Sampled);
        fixture.Peers.Add(newcomer);
        BlockImportResult second = await orchestrator.ImportBlockAsync(block, token);

        slow.Answer(fixture.Sampled.Select(c => fixture.Chain.Columns[(int)c]));
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((first, second), Is.EqualTo((BlockImportResult.DataUnavailable, BlockImportResult.DataUnavailable)));
        Assert.That(inFlightWhileAsked, Is.EqualTo(1));
        Assert.That(slow.Requests, Is.EqualTo(1));
        Assert.That(newcomer.RootColumnRequests, Is.Zero, "one fetch in flight per block");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the fetched columns import the block without a tick");
        Assert.That(orchestrator.ColumnFetchesInFlight, Is.Zero);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Fetched_columns_reach_the_importer_only_as_queued_work(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        fixture.Peers.Add(fixture.Peer("custodian", fixture.Sampled));
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();

        BlockImportResult deferred = await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);
        bool importedOffTheWorker = fixture.Importer.IsKnown(fixture.Chain.BlockRoot);
        int queued = orchestrator.QueuedWorkCount;
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deferred, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(importedOffTheWorker, Is.False);
        Assert.That(queued, Is.Positive);
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True);
    }

    /// <summary>A fetch that fails, or faults before it asks anyone, must not leave the block waiting on it: the slot tick retries and fetches again.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_failed_by_root_fetch_leaves_the_tick_retry_working([Values] bool faults, CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        AsyncRootPeer failing = new("failing", fixture.Sampled, fixture.Chain.Block.Message!.Slot, faultsBeforeAsking: faults);
        fixture.Peers.Add(failing);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);

        BlockImportResult deferred = await orchestrator.ImportBlockAsync(block, token);
        failing.Fail();
        await orchestrator.SettleColumnFetchesAsync(token);
        bool importedByFailedFetch = fixture.Importer.IsKnown(fixture.Chain.BlockRoot);
        int inFlightAfterFailure = orchestrator.ColumnFetchesInFlight;

        fixture.Peers.Clear();
        fixture.Peers.Add(fixture.Peer("honest", fixture.Sampled));
        fixture.AdvanceSlots(1);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.SettleColumnFetchesAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deferred, Is.EqualTo(BlockImportResult.DataUnavailable));
        Assert.That(importedByFailedFetch, Is.False);
        Assert.That(inFlightAfterFailure, Is.Zero, "the failed fetch does not block the next one");
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "the tick's retry fetched and imported");
    }

    /// <summary>The live case: a head block still deferred at the next tick keeps its wake, so its last column imports it before the tick after.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_block_still_deferred_after_a_tick_imports_when_gossip_completes_its_columns(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);

        fixture.AdvanceSlots(1);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        await orchestrator.SettleColumnFetchesAsync(token);
        bool importedByTick = fixture.Importer.IsKnown(fixture.Chain.BlockRoot);
        foreach (ulong column in fixture.Sampled)
        {
            fixture.GiveColumn(column);
        }

        await orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importedByTick, Is.False);
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.True, "imported with no second tick");
        Assert.That(fixture.SidecarPool.WatchCount, Is.Zero);
    }

    /// <summary>Every sampled column held but none verifying must not feed the worker: the retry a wake or a tick runs ends the wait instead of fetching again.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Columns_that_are_held_but_do_not_verify_do_not_make_the_worker_retry_in_a_loop(CancellationToken token)
    {
        await using Fixture fixture = Fixture.Create();
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block);
        await orchestrator.ImportAndSettleAsync(fixture.Importer, block, token);

        foreach (ulong column in fixture.Sampled)
        {
            fixture.SidecarPool.Add(fixture.Chain.BlockRoot, fixture.Chain.Block.Message!.Slot, new DataColumnSidecar { Index = column });
        }

        int passesAfterWake = await orchestrator.SettleWithinAsync(4, token);
        fixture.AdvanceSlots(1);
        await orchestrator.ProcessSlotAsync(fixture.Clock.CurrentSlot, token);
        int passesAfterTick = await orchestrator.SettleWithinAsync(4, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(passesAfterWake, Is.Positive);
        Assert.That(passesAfterTick, Is.LessThanOrEqualTo(4));
        Assert.That(fixture.Importer.IsKnown(fixture.Chain.BlockRoot), Is.False);
    }

    /// <summary>A custodian whose by-root answer is held until the test gives it, or that faults before it is asked.</summary>
    private sealed class AsyncRootPeer(string id, ulong[] custodied, ulong headSlot, bool faultsBeforeAsking = false) : IBeaconSyncPeer
    {
        private readonly TaskCompletionSource<IReadOnlyList<DataColumnSidecar>> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public string Id => id;

        public ulong HeadSlot => headSlot;

        public PeerColumnCustody Custody => faultsBeforeAsking ? throw new InvalidOperationException($"{id} has no custody") : new(custodied, isAdvertised: true);

        public void Answer(IEnumerable<DataColumnSidecar> sidecars) => _answer.TrySetResult([.. sidecars]);

        public void Fail() => _answer.TrySetException(new TimeoutException($"{id} did not answer"));

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            return _answer.Task;
        }

        public void ReportFailure(PeerFailureReason reason, string? detail = null) { }

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token) => throw new NotSupportedException();
    }
}
