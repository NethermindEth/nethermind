// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Network.Config;
using Nethermind.State;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SynchronizerModuleTests
{
    public IContainer CreateTestContainer()
    {
        ITreeSync treeSync = Substitute.For<ITreeSync>();

        return new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddModule(new SynchronizerModule(new TestSyncConfig()
            {
                FastSync = true,
                VerifyTrieOnStateSyncFinished = true
            }))
            .AddSingleton(treeSync)
            .AddSingleton(Substitute.For<IWorldStateManager>())
            .Build();
    }

    [Test]
    public async Task TestOnTreeSyncFinish_CallVisit()
    {
        IContainer ctx = CreateTestContainer();
        ISyncFeed<StateSyncBatch> _ = ctx.Resolve<ISyncFeed<StateSyncBatch>>();
        ITreeSync treeSync = ctx.Resolve<ITreeSync>();
        IWorldStateManager worldStateManager = ctx.Resolve<IWorldStateManager>();

        BlockHeader header = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject;
        treeSync.SyncCompleted += Raise.EventWith(null, new ITreeSync.SyncCompletedEventArgs(header));
        treeSync.SyncCompleted += Raise.EventWith(null, new ITreeSync.SyncCompletedEventArgs(header));

        await Task.Delay(100);

        worldStateManager
            .Received(1)
            .VerifyTrie(Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void SyncPeerPool_should_use_INetworkConfig_MaxActivePeers()
    {
        NetworkConfig networkConfig = new() { MaxActivePeers = 75 };

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(networkConfig))
            .AddModule(new SynchronizerModule(new TestSyncConfig()))
            .AddSingleton(Substitute.For<ITreeSync>())
            .AddSingleton(Substitute.For<IWorldStateManager>())
            .Build();

        SyncPeerPool pool = container.Resolve<SyncPeerPool>();

        Assert.That(pool.PeerMaxCount, Is.EqualTo(75));
    }

    [Test]
    public void Block_access_lists_feed_should_be_active_when_fast_bodies_are_disabled()
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            DownloadHeadersInFastSync = true,
            DownloadBodiesInFastSync = false,
            DownloadBlockAccessListsInFastSync = true
        };

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(syncConfig)))
            .AddModule(new SynchronizerModule(syncConfig))
            .AddSingleton(Substitute.For<ITreeSync>())
            .AddSingleton(Substitute.For<IWorldStateManager>())
            .Build();

        ISyncFeed<BlockAccessListsSyncBatch> feed = container.Resolve<ISyncFeed<BlockAccessListsSyncBatch>>();

        Assert.That(feed, Is.TypeOf<BlockAccessListsSyncFeed>());
    }
}
