// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class E2ESyncTests
{

    [Test]
    public void E2ESyncTest()
    {
        IConfigProvider configProvider = new ConfigProvider();

        IContainer container = new ContainerBuilder()
            .AddSingleton(configProvider)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSource(new ConfigRegistrationSource())
            .AddModule(new SynchronizerModule(configProvider.GetConfig<ISyncConfig>()))
            .Build();

        ISynchronizer synchronizer = container.Resolve<ISynchronizer>();
    }
}
