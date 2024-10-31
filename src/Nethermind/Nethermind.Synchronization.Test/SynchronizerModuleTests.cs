// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            .AddModule(new SynchronizerModule(new SyncConfig()
            {
                FastSync = true,
                VerifyTrieOnStateSyncFinished = true
            }))
            .AddKeyedSingleton(DbNames.Code, Substitute.For<IDb>())
            .AddSingleton(stateReader)
            .AddSingleton(treeSync)
            .AddSingleton(blockQueue)
            .AddSingleton(Substitute.For<IProcessExitSource>())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();
    }

    [Test]
    public void TestOnTreeSyncFinish_CallVisit()
    {
        IContainer ctx = CreateTestContainer();
        ITreeSync treeSync = ctx.Resolve<ITreeSync>();
        IStateReader stateReader = ctx.Resolve<IStateReader>();

        treeSync.OnVerifyPostSyncCleanup += Raise.EventWith(null, new ITreeSync.VerifyPostSyncCleanupEventArgs(TestItem.KeccakA));

        stateReader
            .Received()
            .RunTreeVisitor(Arg.Any<ITreeVisitor>(), Arg.Is(TestItem.KeccakA), Arg.Any<VisitingOptions>());
    }

    [Test]
    public async Task TestOnTreeSyncFinish_BlockProcessingQueue_UntilFinished()
    {
        IContainer ctx = CreateTestContainer();
        ITreeSync treeSync = ctx.Resolve<ITreeSync>();
        IStateReader stateReader = ctx.Resolve<IStateReader>();
        IBlockProcessingQueue blockQueue = ctx.Resolve<IBlockProcessingQueue>();

        ManualResetEvent treeVisitorBlocker = new ManualResetEvent(false);

        stateReader
            .When(sr => sr.RunTreeVisitor(Arg.Any<ITreeVisitor>(), Arg.Is(TestItem.KeccakA), Arg.Any<VisitingOptions>()))
            .Do((ci) =>
            {
                treeVisitorBlocker.WaitOne();
            });

        Task triggerTask = Task.Run(() =>
        {
            treeSync.OnVerifyPostSyncCleanup += Raise.EventWith(null, new ITreeSync.VerifyPostSyncCleanupEventArgs(TestItem.KeccakA));
        });

        await Task.Delay(100);

        Task blockQueueTask = Task.Run(() =>
        {
            blockQueue.BlockRemoved +=
                Raise.EventWith(null, new BlockRemovedEventArgs(null!, ProcessingResult.Success));
        });

        await Task.Delay(100);

        blockQueueTask.IsCompleted.Should().BeFalse();
        treeVisitorBlocker.Set();

        await triggerTask;
        await blockQueueTask;
        blockQueue.BlockRemoved += Raise.EventWith(null, new BlockRemovedEventArgs(null!, ProcessingResult.Success));
    }
}
