// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoModuleTests
{
    [Test]
    public void Payload_transaction_count_guard_is_disabled()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(Substitute.For<IBlockFinder>())
            .AddModule(new TaikoModule())
            .Build();

        Assert.That(container.Resolve<PayloadTransactionCountGuard>(), Is.SameAs(PayloadTransactionCountGuard.Disabled));
    }
}
