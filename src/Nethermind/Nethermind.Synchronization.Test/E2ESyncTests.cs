// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain.Spec;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.Test.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public partial class E2ESyncTests
{

    [Test]
    public void E2ESyncTest()
    {
        IConfigProvider configProvider = new ConfigProvider();

        IContainer container = new ContainerBuilder()
            .AddModule(new PsudoNethermindModule(configProvider))
            .Build();

        ISynchronizer synchronizer = container.Resolve<ISynchronizer>();
    }
}
