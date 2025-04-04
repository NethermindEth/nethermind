// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
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
}
