// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    [Test]
    public void Initialization_publishes_the_anchor_as_head_and_finalized([Values] bool captureState)
    {
        HeadSnapshotHolder snapshots = new() { CaptureState = captureState };
        ForkedBeaconState state = new ForkedBeaconState.OfFulu(new BeaconStateFulu());
        int reads = 0;
        Harness harness = CreateHarness(headSnapshots: snapshots, apiStateReader: _ => { reads++; return state; },
            apiCheckpointStateReader: _ => throw new InvalidOperationException("Checkpoint advancement ran before pubkey-cache initialization"));

        HeadSnapshot? published = snapshots.Current;

        Assert.That(published, Is.Not.Null);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(published!.Status.HeadRoot, Is.EqualTo(harness.StatusHolder.CurrentStatus.HeadRoot));
        Assert.That(published.Status.FinalizedRoot, Is.EqualTo(published.Status.HeadRoot), "a node that has only its anchor has it as head and finalized");
        Assert.That(published.JustifiedRoot, Is.EqualTo(Hash256.Zero), "no head step has named a justified checkpoint yet");
        Assert.That(published.ExecutionInSync, Is.False);
        Assert.That(published.FullHeadRoot, Is.Null);
        Assert.That(published.State, Is.SameAs(captureState ? state : null));
        Assert.That(published.FinalizedState, Is.Null, "checkpoint advancement must wait until after the engine kick and pubkey-cache initialization");
        Assert.That(reads, Is.EqualTo(captureState ? 1 : 0));
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
    public async Task A_head_step_caches_head_states_by_root_and_checkpoint_states_by_root_and_epoch()
    {
        HeadSnapshotHolder snapshots = new() { CaptureState = true };
        Harness harness = CreateHarness(headSnapshots: snapshots);
        List<(Hash256 Root, ForkedBeaconState State)> reads = [];
        harness.Orchestrator.ApiStateReader = root =>
        {
            ForkedBeaconState state = new ForkedBeaconState.OfFulu(new BeaconStateFulu());
            reads.Add((root, state));
            return state;
        };
        List<CheckpointRef> checkpointReads = [];
        harness.Orchestrator.ApiCheckpointStateReader = checkpoint =>
        {
            checkpointReads.Add(checkpoint);
            BeaconStateFulu state = GloasTestFixtures.CreateFuluState(1);
            state.Slot = checkpoint.Epoch * Spec.SlotsPerEpoch;
            return new ForkedBeaconState.OfFulu(state);
        };
        Hash256[] heads = [TestItem.KeccakA, TestItem.KeccakA, TestItem.KeccakB];
        ForkedBeaconState?[] published = new ForkedBeaconState?[heads.Length];
        for (int i = 0; i < heads.Length; i++)
        {
            harness.Importer.Head = CreateHead(heads[i], AnchorSlot + 1 + (ulong)i, finalizedEpoch: i == 2 ? 4UL : 3UL);
            await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
            published[i] = snapshots.Current!.State;
        }

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reads.Select(r => r.Root), Is.EqualTo(new[] { TestItem.KeccakA, TestItem.KeccakB }), "a step that keeps a root keeps its state");
        Assert.That(published[0], Is.Not.Null.And.SameAs(reads[0].State), "the state is published with the head, never taken later on the import thread");
        Assert.That(published[1], Is.SameAs(published[0]));
        Assert.That(published[2], Is.SameAs(reads[1].State));
        Assert.That(checkpointReads, Is.EqualTo(new[] { new CheckpointRef(4, TestItem.KeccakC), new CheckpointRef(3, TestItem.KeccakD), new CheckpointRef(5, TestItem.KeccakC), new CheckpointRef(4, TestItem.KeccakD) }));
        Assert.That(snapshots.Current!.JustifiedState!.State.Slot, Is.EqualTo(5 * Spec.SlotsPerEpoch));
        Assert.That(snapshots.Current.FinalizedState!.State.Slot, Is.EqualTo(4 * Spec.SlotsPerEpoch));
        Assert.That(snapshots.Current.JustifiedState.StateRoot, Is.EqualTo(SszRoots.HashTreeRoot(((ForkedBeaconState.OfFulu)snapshots.Current.JustifiedState.State).State)));
        Assert.That(snapshots.Current.FinalizedState.StateRoot, Is.EqualTo(SszRoots.HashTreeRoot(((ForkedBeaconState.OfFulu)snapshots.Current.FinalizedState.State).State)));
    }

    [Test]
    public async Task A_head_state_the_previous_step_did_not_find_is_read_again_by_the_next_step()
    {
        HeadSnapshotHolder snapshots = new() { CaptureState = true };
        Harness harness = CreateHarness(headSnapshots: snapshots);
        int reads = 0;
        harness.Orchestrator.ApiStateReader = root => root != TestItem.KeccakA ? null : ++reads == 1 ? null : new ForkedBeaconState.OfFulu(new BeaconStateFulu());
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot + 1, finalizedEpoch: 3);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        ForkedBeaconState? missed = snapshots.Current!.State;
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(missed, Is.Null);
        Assert.That(snapshots.Current!.State, Is.Not.Null, "a missing state must not pin the head's API state to null");
        Assert.That(reads, Is.EqualTo(2));
    }
}
