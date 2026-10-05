// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
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

    // Never start the service: this race must not reach network or socket code.
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
    public void Start_stamps_an_unversioned_database_before_reading_its_anchor()
    {
        using IContainer container = BuildContainer();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        store.SetAnchor(TestItem.KeccakA, 1);

        Assert.That(() => container.Resolve<BeaconChainService>().Start(), Throws.InvalidOperationException.With.Message.Contains("anchor state"), "the driver went on to read the anchor");
        Assert.That(store.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(BeaconChainStore.CurrentSchemaVersion));
    }

    [Test]
    public async Task Engine_stall_does_not_hold_shutdown_or_dispose_the_run_token()
    {
        using IContainer container = BuildContainer();
        using BeaconChainService service = new(container.Resolve<IBeaconChainConfig>(), container.Resolve<BeaconChainSpec>(),
            container.Resolve<BeaconChainStore>(), container.Resolve<PubkeyCache>(), container.Resolve<CheckpointSync>(),
            container.Resolve<BeaconSyncOrchestrator>(), container.Resolve<ExternalClDetector>(), LimboLogs.Instance)
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(20),
        };
        TaskCompletionSource run = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(BeaconChainService).GetField("_runTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, run.Task);
        CancellationTokenSource source = (CancellationTokenSource)typeof(BeaconChainService)
            .GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        try
        {
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            service.Dispose();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.Token.IsCancellationRequested, Is.True);
                Assert.That(run.Task.IsCompleted, Is.False);
            }
        }
        finally
        {
            run.TrySetResult();
        }
        Assert.That(() => source.Token, Throws.TypeOf<ObjectDisposedException>().After(2000, 10));
    }

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
