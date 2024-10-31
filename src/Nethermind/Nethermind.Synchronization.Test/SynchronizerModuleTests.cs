// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Synchronization;
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
    [Test]
    public void TestOnTreeSyncFinish_CallVisit()
    {
        ITreeSync treeSync = Substitute.For<ITreeSync>();
        IStateReader stateReader = Substitute.For<IStateReader>();

        using IContainer container = new ContainerBuilder()
            .AddModule(new SynchronizerModule(new SyncConfig()
            {
                FastSync = true,
                VerifyTrieOnStateSyncFinished = true
            }))
            .AddKeyedSingleton(DbNames.Code, Substitute.For<IKeyValueStore>())
            .AddSingleton(stateReader)
            .AddSingleton(treeSync)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();

        treeSync.OnVerifyPostSyncCleanup += Raise.EventWith(null, new ITreeSync.PostSyncCleanupEventArgs(TestItem.KeccakA));

        stateReader.Received().RunTreeVisitor(Arg.Any<ITreeVisitor>(), Arg.Is(TestItem.KeccakA), Arg.Any<VisitingOptions>());
    }
}
