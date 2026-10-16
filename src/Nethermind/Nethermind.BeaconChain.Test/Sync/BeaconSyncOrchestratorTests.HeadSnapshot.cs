// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    [Test]
    public void Initialization_publishes_the_anchor_as_head_and_finalized()
    {
        HeadSnapshotHolder snapshots = new();
        Harness harness = CreateHarness(headSnapshots: snapshots);

        HeadSnapshot? published = snapshots.Current;

        Assert.That(published, Is.Not.Null);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(published!.Status.HeadRoot, Is.EqualTo(harness.StatusHolder.CurrentStatus.HeadRoot));
        Assert.That(published.Status.FinalizedRoot, Is.EqualTo(published.Status.HeadRoot), "a node that has only its anchor has it as head and finalized");
        Assert.That(published.JustifiedRoot, Is.EqualTo(Hash256.Zero), "no head step has named a justified checkpoint yet");
        Assert.That(published.ExecutionInSync, Is.False);
        Assert.That(published.FullHeadRoot, Is.Null);
    }

    [Test]
    public async Task A_head_step_publishes_a_new_snapshot_and_leaves_the_one_a_reader_holds_unchanged([Values] bool executionValid, [Values] bool fullPayload)
    {
        HeadSnapshotHolder snapshots = new();
        Harness harness = CreateHarness(headSnapshots: snapshots);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 103, finalizedEpoch: 3) with { HeadPayloadFull = fullPayload };
        harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = executionValid ? PayloadStatus.Valid : PayloadStatus.Syncing, LatestValidHash = TestItem.KeccakG });
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        HeadSnapshot held = snapshots.Current!;

        harness.Importer.Head = CreateHead(TestItem.KeccakB, 104, finalizedEpoch: 4, execHash: TestItem.KeccakD) with { HeadPayloadFull = !fullPayload };
        harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = executionValid ? PayloadStatus.Syncing : PayloadStatus.Valid, LatestValidHash = TestItem.KeccakD });
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        HeadSnapshot next = snapshots.Current!;

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(next, Is.Not.SameAs(held));
        Assert.That(held.FullHeadRoot, Is.EqualTo(fullPayload ? TestItem.KeccakA : null));
        Assert.That(next.FullHeadRoot, Is.EqualTo(fullPayload ? null : TestItem.KeccakB));
        Assert.That(held.Status.HeadRoot, Is.EqualTo(TestItem.KeccakA), "held head");
        Assert.That(held.Status.FinalizedEpoch, Is.EqualTo(3UL), "held finality");
        Assert.That(held.JustifiedRoot, Is.EqualTo(TestItem.KeccakC), "held justified root");
        Assert.That(held.ExecutionInSync, Is.EqualTo(executionValid), "held execution flag");
        Assert.That(next.Status.HeadRoot, Is.EqualTo(TestItem.KeccakB));
        Assert.That(next.Status.FinalizedEpoch, Is.EqualTo(4UL));
        Assert.That(next.ExecutionInSync, Is.EqualTo(!executionValid), "the flag follows the same step's forkchoiceUpdated verdict");
        Assert.That(next.Status.HeadRoot, Is.EqualTo(harness.StatusHolder.CurrentStatus.HeadRoot), "peers and the API are told the same head");
    }

    [Test]
    public async Task Head_changes_with_no_api_reader_copy_no_state_and_the_first_read_copies_it_once()
    {
        HeadSnapshotHolder snapshots = new() { CaptureState = true };
        Harness harness = CreateHarness(headSnapshots: snapshots);
        int copies = 0;
        harness.Orchestrator.ApiStateReader = _ =>
        {
            Interlocked.Increment(ref copies);
            return new ForkedBeaconState.OfFulu(new BeaconStateFulu());
        };
        for (int i = 0; i < 10; i++)
        {
            harness.Importer.Head = CreateHead(Keccak.Compute($"head {i}"), AnchorSlot + 1 + (ulong)i, finalizedEpoch: 3);
            await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        }

        int copiesWithoutReader = copies;
        HeadSnapshot published = snapshots.Current!;
        ForkedBeaconState? unread = published.State;
        ForkedBeaconState? first = await published.DeferredState!.TakeAsync();
        ForkedBeaconState? second = await published.DeferredState.TakeAsync();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(copiesWithoutReader, Is.Zero, "a head change must not copy the full state on the import worker when no request reads it");
        Assert.That(unread, Is.Null, "reading the snapshot's state never starts the copy");
        Assert.That(copies, Is.EqualTo(1));
        Assert.That(second, Is.Not.Null.And.SameAs(first), "every read of one head shares one copy");
        Assert.That(published.State, Is.SameAs(first));
    }

    [Test]
    public async Task A_head_state_copy_that_finds_no_state_is_taken_again_by_the_next_reader([Values] bool throws)
    {
        HeadSnapshotHolder snapshots = new() { CaptureState = true };
        Harness harness = CreateHarness(headSnapshots: snapshots);
        int copies = 0;
        harness.Orchestrator.ApiStateReader = _ => Interlocked.Increment(ref copies) switch
        {
            1 when throws => throw new InvalidOperationException("state evicted"),
            1 => null,
            _ => new ForkedBeaconState.OfFulu(new BeaconStateFulu()),
        };
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot + 1, finalizedEpoch: 3);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        DeferredHeadState deferred = snapshots.Current!.DeferredState!;

        ForkedBeaconState? missed = await deferred.TakeAsync();
        ForkedBeaconState? retried = await deferred.TakeAsync();
        ForkedBeaconState? kept = await deferred.TakeAsync();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(missed, Is.Null);
        Assert.That(retried, Is.Not.Null, "a copy that found no state must not pin the head's API state to null");
        Assert.That(kept, Is.SameAs(retried));
        Assert.That(copies, Is.EqualTo(2));
    }
}
