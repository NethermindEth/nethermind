// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Sync;
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

}
