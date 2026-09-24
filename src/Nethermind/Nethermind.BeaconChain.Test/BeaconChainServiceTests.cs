// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainServiceTests
{
    private static IContainer BuildContainer(ILogManager? logManager = null, ulong chainId = BlockchainIds.Mainnet) =>
        BeaconChainTestContainer.Builder(chainId, logManager).Build();

    // Regression for gap 113: Stop() (re-entered from the ExternalClDetected event, raised on
    // whatever thread serviced the engine call) used to check `_disposed` and cancel the token
    // source as two unsynchronised steps. A concurrent Dispose() (container teardown) could pass
    // its own check, set the flag and dispose the source in between, so Stop()'s Cancel() call
    // landed on an already-disposed CancellationTokenSource and threw ObjectDisposedException
    // instead of shutting down quietly. Never calls Start(), matching the note that this stream
    // must not let a real orchestrator run reach network/socket code.
    [Test]
    public void Stop_and_Dispose_do_not_throw_when_invoked_concurrently()
    {
        using IContainer container = BuildContainer();
        IBeaconChainConfig config = container.Resolve<IBeaconChainConfig>();
        BeaconChainSpec spec = container.Resolve<BeaconChainSpec>();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        PubkeyCache pubkeyCache = container.Resolve<PubkeyCache>();
        CheckpointSync checkpointSync = container.Resolve<CheckpointSync>();
        BeaconSyncOrchestrator orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        ExternalClDetector externalClDetector = container.Resolve<ExternalClDetector>();
        ILogManager logManager = container.Resolve<ILogManager>();

        for (int i = 0; i < 300; i++)
        {
            BeaconChainService service = new(config, spec, store, pubkeyCache, checkpointSync, orchestrator, externalClDetector, logManager);
            using Barrier barrier = new(2);
            Task stopTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                service.Stop();
            });
            Task disposeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                service.Dispose();
            });

            Assert.DoesNotThrowAsync(async () => await Task.WhenAll(stopTask, disposeTask));
        }
    }

    // A store resolved without the spec would silently keep the Fulu-only shape and refuse every Gloas block.
    [Test]
    public void The_resolved_store_stores_blocks_in_the_shape_of_the_network_fork_schedule()
    {
        using IContainer container = BuildContainer(chainId: BlockchainIds.Sepolia);
        BeaconChainSpec spec = container.Resolve<BeaconChainSpec>();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        ulong firstGloasSlot = spec.GloasForkEpoch * spec.SlotsPerEpoch;

        store.PutForkedBlock(TestItem.KeccakA, new ForkedSignedBeaconBlock.OfGloas(SignedBeaconBlockBuilders.CreateMinimalGloasBlock(firstGloasSlot)));

        Assert.That(store.TryGetForkedBlock(TestItem.KeccakA, out ForkedSignedBeaconBlock? block), Is.True);
        Assert.That(block, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>());
    }

    [Test]
    public async Task Start_refuses_a_database_written_by_a_newer_schema_version_before_reading_the_anchor()
    {
        TestErrorLogManager logManager = new();
        using IContainer container = BuildContainer(logManager);
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        uint newer = BeaconChainStore.CurrentSchemaVersion + 1;
        store.SetSchemaVersion(newer);
        store.SetAnchor(TestItem.KeccakA, 1);

        await container.Resolve<BeaconChainService>().Start();

        Assert.That(logManager.Errors.Single().Exception?.Message, Does.Contain($"schema version {newer}"), "the driver must stop at the version check, not at the anchor it would otherwise misread");
        Assert.That(store.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(newer), "a refused database is not restamped");
    }

    [Test]
    public async Task Start_stamps_an_unversioned_database_before_reading_its_anchor()
    {
        TestErrorLogManager logManager = new();
        using IContainer container = BuildContainer(logManager);
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        // An anchor without its state stops the driver right after the version check, before any network access.
        store.SetAnchor(TestItem.KeccakA, 1);

        await container.Resolve<BeaconChainService>().Start();

        Assert.That(store.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(BeaconChainStore.CurrentSchemaVersion));
        Assert.That(logManager.Errors.Single().Exception?.Message, Does.Contain("anchor state"), "the driver went on to read the anchor");
    }

    // Regression for gap 113: ServiceStopper.StopAllServices() resolves every registered
    // IStoppableService and awaits its StopAsync(), including a driver that was resolved into the
    // container but never started (e.g. an earlier startup step failed before StartBeaconChain
    // ran). Before the fix BeaconChainService did not implement IStoppableService at all, so
    // shutdown could only reach the synchronous Stop() and Dispose() never had anything to await.
    [Test]
    public async Task StopAsync_on_an_unstarted_service_does_not_throw_and_still_allows_dispose()
    {
        using IContainer container = BuildContainer();
        BeaconChainService service = container.Resolve<BeaconChainService>();

        Assert.That(service, Is.InstanceOf<IStoppableService>());

        await ((IStoppableService)service).StopAsync();

        Assert.DoesNotThrow(() => service.Dispose());
    }
}
