// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using NSubstitute;

namespace Nethermind.Synchronization.Test;

public class TestSynchronizerModule(ISyncConfig syncConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new SynchronizerModule(syncConfig))
            .AddModule(new DbModule())
            .AddSingleton<IDbProvider>(TestMemDbProvider.Init())
            .Map<IDbProvider, INodeStorage>(dbProvider => new NodeStorage(dbProvider.StateDb))
            .AddSingleton<IBlockTree>(Substitute.For<IBlockTree>())
            .AddSingleton<ISyncConfig>(syncConfig)
            .AddSingleton<ILogManager>(LimboLogs.Instance);

    }
}
