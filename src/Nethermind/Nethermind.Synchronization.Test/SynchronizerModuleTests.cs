// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SynchronizerModuleTests
{
    public IContainer CreateTestContainer()
    {
        ITreeSync treeSync = Substitute.For<ITreeSync>();
        IStateReader stateReader = Substitute.For<IStateReader>();
        IBlockProcessingQueue blockQueue = Substitute.For<IBlockProcessingQueue>();

        return new ContainerBuilder()
            .AddModule(new SynchronizerModule(new TestSyncConfig()
            {
                FastSync = true,
                VerifyTrieOnStateSyncFinished = true
            }))
            .AddKeyedSingleton(DbNames.Code, Substitute.For<IDb>())
            .AddSingleton(stateReader)
            .AddSingleton(treeSync)
            .AddSingleton(blockQueue)
            .AddSingleton(Substitute.For<IWorldStateManager>())
            .AddSingleton(Substitute.For<IProcessExitSource>())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();
    }

    [Test]
    public async Task TestOnTreeSyncFinish_CallVisit()
    {
        IContainer ctx = CreateTestContainer();
        ITreeSync treeSync = ctx.Resolve<ITreeSync>();
        IWorldStateManager worldStateManager = ctx.Resolve<IWorldStateManager>();

        BlockHeader header = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject;
        treeSync.SyncCompleted += Raise.EventWith(null, new ITreeSync.SyncCompletedEventArgs(header));
        treeSync.SyncCompleted += Raise.EventWith(null, new ITreeSync.SyncCompletedEventArgs(header));

        await Task.Delay(100);

        worldStateManager
            .Received(1)
            .TryStartVerifyTrie(Arg.Any<BlockHeader>());
    }
}
