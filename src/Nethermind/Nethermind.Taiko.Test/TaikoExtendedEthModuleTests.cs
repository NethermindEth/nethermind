// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.Taiko.Rpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoExtendedEthModuleTests
{
    [Test]
    public void TestCanResolve()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddModule(new TaikoModule())
            .Build();

        var act = () => container.Resolve<ITaikoExtendedEthRpcModule>();
        act.Should().NotThrow();
    }

    [TestCase(true, "snap")]
    [TestCase(false, "full")]
    public void TestSyncMode(bool snapEnabled, string result)
    {
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig()
        {
            SnapSync = snapEnabled
        }, Substitute.For<IL1OriginStore>());

        rpc.taiko_getSyncMode().Result.Data.Should().Be(result);
    }

    [Test]
    public void TestHeadL1Origin()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig(), originStore);

        L1Origin origin = new L1Origin(0, TestItem.KeccakA, 1, Hash256.Zero, null);
        originStore.ReadHeadL1Origin().Returns((UInt256)1);
        originStore.ReadL1Origin((UInt256)1).Returns(origin);

        rpc.taiko_headL1Origin().Result.Data.Should().Be(origin);
    }

    [Test]
    public void TestL1OriginById()
    {
        IL1OriginStore originStore = Substitute.For<IL1OriginStore>();
        TaikoExtendedEthModule rpc = new TaikoExtendedEthModule(new SyncConfig(), originStore);

        L1Origin origin = new L1Origin(0, TestItem.KeccakA, 1, Hash256.Zero, null);
        originStore.ReadL1Origin((UInt256)0).Returns(origin);

        rpc.taiko_l1OriginByID(0).Result.Data.Should().Be(origin);
    }
}
