// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    [Test]
    [CancelAfter(30_000)]
    public async Task A_gloas_envelope_waiting_for_its_columns_imports_when_they_arrive_without_waiting_for_a_tick([Values] bool asParkedCandidates, CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        (Harness harness, DataColumnSidecarPool sidecars, Hash256 root, IReadOnlyList<ulong> sampled, ExecutionPayloadEnvelopeImportResult? waiting) = await DeferGloasEnvelopeAsync(discovery, token);
        int importsBeforeColumns = harness.Importer.Envelopes.Count;
        int ticksBeforeColumns = harness.Importer.Ticks.Count;

        foreach (ulong column in sampled)
        {
            sidecars.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, ColumnSlot, TestItem.KeccakB));
        }

        int queuedForUnrelatedRoot = harness.Orchestrator.QueuedWorkCount;
        for (int i = 0; i < sampled.Count; i++)
        {
            DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(sampled[i], ColumnSlot, root);
            if (asParkedCandidates)
            {
                sidecars.AddPendingGloas(sidecar, ColumnSlot);
            }
            else
            {
                sidecars.AddGloas(sidecar);
            }

            Assert.That(harness.Orchestrator.QueuedWorkCount, Is.EqualTo(i == sampled.Count - 1 ? 1 : 0), "only the last awaited column queues the retry");
        }

        await harness.Orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(waiting, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.DataUnavailable));
        Assert.That(queuedForUnrelatedRoot, Is.Zero);
        Assert.That(importsBeforeColumns, Is.EqualTo(1));
        Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(2), "retried once the columns were held");
        Assert.That(harness.EnvelopePool.TryGet(root, out _), Is.True, "the retry recorded the payload");
        Assert.That(harness.Importer.Ticks, Has.Count.EqualTo(ticksBeforeColumns), "no slot tick ran");
        Assert.That(sidecars.WatchCount, Is.Zero);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_gloas_envelope_whose_unreached_column_is_already_parked_imports_when_the_rest_arrive(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        DiscoveryNodeCustodySource source = new(discovery);
        ulong firstCustody = source.Current!.CustodyColumns[0];
        IReadOnlyList<ulong> sampled = source.Current!.SampledColumns;
        ulong parked = sampled.Last(c => c != firstCustody);
        (Harness harness, DataColumnSidecarPool sidecars, Hash256 root, _, ExecutionPayloadEnvelopeImportResult? waiting) = await DeferGloasEnvelopeAsync(discovery, token, beforeEnvelope: (pool, blockRoot) => pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(parked, ColumnSlot, blockRoot), ColumnSlot));

        foreach (ulong column in sampled.Where(c => c != parked))
        {
            sidecars.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, ColumnSlot, root));
        }

        int queued = harness.Orchestrator.QueuedWorkCount;
        await harness.Orchestrator.ProcessQueuedAsync(token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(waiting, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.DataUnavailable));
        Assert.That(queued, Is.EqualTo(1), "the parked column is not waited for again");
        Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(2), "retried with no tick");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_gloas_envelopes_pool_watch_ends_when_its_retry_expires(CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        (Harness harness, DataColumnSidecarPool sidecars, _, _, _) = await DeferGloasEnvelopeAsync(discovery, token);
        int whileParked = sidecars.WatchCount;

        await TickAtAgeAsync(harness, RetryAgeSlots + 1, () => 0);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(whileParked, Is.EqualTo(1));
        Assert.That(sidecars.WatchCount, Is.Zero);
    }

    private async Task<(Harness Harness, DataColumnSidecarPool Sidecars, Hash256 Root, IReadOnlyList<ulong> Sampled, ExecutionPayloadEnvelopeImportResult? Waiting)> DeferGloasEnvelopeAsync(
        BeaconDiscovery discovery, CancellationToken token, Action<DataColumnSidecarPool, Hash256>? beforeEnvelope = null)
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock block = GloasBlobBlock(EnvelopeBlockSlot, anchorRoot, ColumnSlot);
        ExecutionPayloadBid bid = ((ForkedSignedBeaconBlock.OfGloas)block).Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        Hash256 root = block.ComputeMessageRoot();
        DataColumnSidecarPool sidecars = new();
        IReadOnlyList<ulong> sampled = new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns;
        Harness harness = CreateHarness(sidecarPool: sidecars, discovery: discovery);
        harness.Importer.Known.Add(anchorRoot);
        GloasCustodySamplingAvailability availability = new(new DiscoveryNodeCustodySource(discovery), sidecars, RangeSyncTests.ClockAtGenesis(Spec), Spec);
        harness.Importer.EnvelopeVerdict = _ => availability.IsDataAvailable(root, bid) ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        await harness.Orchestrator.ImportBlockAsync(block, token);
        beforeEnvelope?.Invoke(sidecars, root);
        ExecutionPayloadEnvelopeImportResult? waiting = await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, EnvelopeBlockSlot), token);
        await harness.Orchestrator.SettleColumnFetchesAsync(token);
        return (harness, sidecars, root, sampled, waiting);
    }
}
