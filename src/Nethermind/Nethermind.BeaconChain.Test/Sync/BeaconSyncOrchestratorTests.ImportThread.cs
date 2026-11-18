// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>The engine call blocks its caller; the import thread must not be a thread-pool thread.</summary>
public partial class BeaconSyncOrchestratorTests
{
    public enum ImportEntryPoint
    {
        RangeBlock,
        GossipBlock,
        Replay,
        Envelope,
        BlockHeldForParentPayload,
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Imports_never_run_on_a_thread_pool_thread([Values] ImportEntryPoint entryPoint, CancellationToken token)
    {
        Harness harness;
        switch (entryPoint)
        {
            case ImportEntryPoint.RangeBlock or ImportEntryPoint.GossipBlock:
                {
                    harness = CreateHarness();
                    (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 101);
                    harness.Importer.Known.Add(anchorRoot);
                    ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
                    harness.Orchestrator.WorkWriter.TryWrite(entryPoint == ImportEntryPoint.RangeBlock
                        ? new BeaconSyncOrchestrator.RangeBlockItem(block)
                        : new BeaconSyncOrchestrator.GossipBlockItem(block));
                    harness.Orchestrator.WorkWriter.Complete();
                    await Task.Run(() => harness.Orchestrator.RunWorkerAsync(token), token);
                    break;
                }

            case ImportEntryPoint.Replay:
                {
                    BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
                    (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 101, 102);
                    TestChain.Persist(store, anchor, anchorRoot, chain);
                    harness = CreateHarness(store: store);
                    harness.Importer.Known.Add(anchorRoot);
                    await Task.Run(() => harness.Orchestrator.ReplayStoredBlocksAsync(token), token);
                    break;
                }

            case ImportEntryPoint.Envelope:
                harness = CreateHarness();
                await Task.Run(() => harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.KeccakB, WallSlot), token), token);
                break;

            default:
                {
                    ParkedParentScenario scenario = CreateParkedParentScenario();
                    harness = scenario.Harness;
                    await Task.Run(async () =>
                    {
                        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Parent, token);
                        await harness.Orchestrator.ProcessGossipBlockAsync(scenario.Child, token);
                    }, token);
                    Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(1), "fixture: the child is held for its parent's payload, which imports it once more");
                    break;
                }
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.ImportedOnPoolThread, Is.Not.Empty);
        Assert.That(harness.Importer.ImportedOnPoolThread, Is.All.False);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task The_import_thread_is_kept_between_imports_that_are_seconds_apart()
    {
        BeaconSyncOrchestrator.ImportThread importThread = new();

        Thread first = await importThread.RunAsync(static () => Thread.CurrentThread);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Thread second = await importThread.RunAsync(static () => Thread.CurrentThread);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task An_import_thread_that_fails_to_start_does_not_hold_back_the_imports_after_it()
    {
        int starts = 0;
        BeaconSyncOrchestrator.ImportThread importThread = new(thread =>
        {
            if (starts++ == 0)
            {
                throw new OutOfMemoryException("no thread");
            }

            thread.Start();
        });

        Assert.Throws<OutOfMemoryException>(() => importThread.RunAsync(static () => 1));
        int second = await importThread.RunAsync(static () => 2).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(second, Is.EqualTo(2));
    }
}
